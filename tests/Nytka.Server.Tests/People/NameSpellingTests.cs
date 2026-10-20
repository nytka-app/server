using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Tests.Search;
using Nytka.Storage;

namespace Nytka.Server.Tests.People;

/// <summary>Renaming or merging a person respells the model's writing about them (docs/specs/name-spelling.md).</summary>
[Collection(PostgresCollection.Name)]
public sealed class NameSpellingTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db, services: s => SearchSeed.WithoutIndexer(s));

    private static readonly DateTime T0 = SearchSeed.T0;

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client => _server.CreateAuthorizedClient();

    [Theory]
    [InlineData("Alena called about Alena's bees. Alenas stay.", "Alena", "Olena", "Olena called about Olena's bees. Alenas stay.")]
    [InlineData("АЛЕНА і алена, Алена", "Алена", "Олена", "АЛЕНА і алена, Олена")]
    [InlineData("Called Olena.", "Alena", "Olena", null)]
    [InlineData("Anna-Maria", "Anna", "Hanna", "Hanna-Maria")]
    [InlineData("I will call. Will phoned, will call back.", "Will", "Oleg", "I will call. Oleg phoned, will call back.")]
    public void Respell_matches_whole_names_and_keeps_case_shape(string text, string oldName, string newName, string? expected) =>
        Assert.Equal(expected, NameSpelling.Respell(text, oldName, newName));

    private async Task<Guid> Person(string name, string speakerId)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/people", new { name, speakerId });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> Spoken(string speakerId, string? title, string? aiTitle, string? summary)
    {
        var conversation = await SearchSeed.ConversationAsync(db.DataSource, T0, title, aiTitle, summary);
        await db.ExecuteAsync(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker_id)
            values (@conversation, (select min(id) from transcription_batches where conversation_id = @conversation), @T0, @T0, 'Alena here', @speakerId)
            """,
            new { conversation, T0, speakerId });
        return conversation;
    }

    private Task AddTask(Guid conversation, string text, bool edited = false, bool done = false, Guid? person = null) =>
        db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, done, edited, person_id, created_at, updated_at)
            values (@id, @conversation, @text, @text, @done, @edited, @person, @T0, @T0)
            """,
            new { id = Guid.CreateVersion7(), conversation, text, edited, done, person, T0 });

    [Fact]
    public async Task A_rename_respells_generated_text_of_the_persons_conversations_only()
    {
        var alena = await Person("Alena", "4");
        await Person("Marko", "5");
        var mine = await Spoken("4", title: "My own Alena title", aiTitle: "Talk with Alena", summary: "Alena sells honey.");
        var markos = await Spoken("5", title: null, aiTitle: "Talk with Alena's friend", summary: "Marko and Alena.");
        await AddTask(mine, "Call Alena");
        await AddTask(mine, "Hand-edited Alena", edited: true);
        await AddTask(mine, "Done with Alena", done: true);
        await AddTask(markos, "Ask Marko about Alena");
        await AddTask(markos, "Owed to Alena", person: alena);

        var response = await Client.PatchAsJsonAsync($"/api/v1/people/{alena}", new { name = "Olena" });
        response.EnsureSuccessStatusCode();

        Assert.Equal("Talk with Olena", await db.ScalarAsync<string>("select ai_title from conversations where id = @mine", new { mine }));
        Assert.Equal("Olena sells honey.", await db.ScalarAsync<string>("select ai_summary from conversations where id = @mine", new { mine }));
        Assert.Equal("My own Alena title", await db.ScalarAsync<string>("select title from conversations where id = @mine", new { mine }));
        Assert.Equal("Talk with Alena's friend", await db.ScalarAsync<string>("select ai_title from conversations where id = @markos", new { markos }));
        Assert.Equal("Marko and Alena.", await db.ScalarAsync<string>("select ai_summary from conversations where id = @markos", new { markos }));
        Assert.Equal(
            ["Ask Marko about Alena", "Call Olena", "Done with Alena", "Hand-edited Alena", "Owed to Olena"],
            (await db.QueryAsync<string>("select text from tasks order by text")).ToArray());
        Assert.Equal("Alena here", await db.ScalarAsync<string>("select text from segments where speaker_id = '4'"));
    }

    [Fact]
    public async Task Renaming_again_with_the_same_name_changes_nothing_and_a_role_person_is_not_respelled()
    {
        var alena = await Person("Alena", "4");
        var conversation = await Spoken("4", null, "Alena", "Alena sells honey.");
        await Client.PatchAsJsonAsync($"/api/v1/people/{alena}", new { name = "Olena" });
        await Client.PatchAsJsonAsync($"/api/v1/people/{alena}", new { name = "Olena" });
        Assert.Equal("Olena", await db.ScalarAsync<string>("select ai_title from conversations where id = @conversation", new { conversation }));

        var role = await db.ScalarAsync<Guid>("insert into people (id, name, created_at, named) values (@id, 'Repairman', @T0, false) returning id", new { id = Guid.NewGuid(), T0 });
        await db.ExecuteAsync("update conversations set ai_title = 'The Repairman came'");
        await db.ExecuteAsync("update segments set person_id = @role", new { role });
        await Client.PatchAsJsonAsync($"/api/v1/people/{role}", new { name = "Mykola" });
        Assert.Equal("The Repairman came", await db.ScalarAsync<string>("select ai_title from conversations where id = @conversation", new { conversation }));
    }

    [Fact]
    public async Task A_merge_respells_the_merged_name_to_the_targets()
    {
        var alena = await Person("Alena", "4");
        var olena = await Person("Olena", "5");
        var conversation = await Spoken("4", null, "Alena and bees", "Alena sells honey.");

        (await Client.PostAsJsonAsync($"/api/v1/people/{alena}/merge", new { intoId = olena })).EnsureSuccessStatusCode();

        Assert.Equal("Olena and bees", await db.ScalarAsync<string>("select ai_title from conversations where id = @conversation", new { conversation }));
        Assert.Equal("Olena sells honey.", await db.ScalarAsync<string>("select ai_summary from conversations where id = @conversation", new { conversation }));
    }
}
