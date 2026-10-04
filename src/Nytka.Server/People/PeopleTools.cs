using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Storage;

namespace Nytka.Server.People;

public sealed record McpPersonList(IReadOnlyList<PersonSummary> Items);

/// <summary><c>GET /api/v1/people/{id}</c> without the voiceprint fields.</summary>
public sealed record McpPerson(
    Guid Id, string Name, string? Note, DateTime CreatedAt, DateTime? LastSeenAt, string[] Voices,
    IReadOnlyList<PersonConversation> Conversations, IReadOnlyList<PersonFactRow> Facts, IReadOnlyList<TaskRow> OpenTasks);

/// <summary>The <c>list_people</c> and <c>get_person</c> tools (docs/specs/people.md, API): what REST returns, no voiceprint field.</summary>
[McpServerToolType]
public sealed class PeopleTools(PeopleStore people)
{
    /// <summary>Like REST, every field is written, null ones too.</summary>
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "list_people", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(McpPersonList))]
    [Description("Lists the people the wearer named, most recently heard first, with when they were last heard and how many facts Nytka holds about them.")]
    public async Task<CallToolResult> ListPeopleAsync(CancellationToken ct = default) =>
        Ok(new McpPersonList(await people.SummariesAsync(ct)));

    [McpServerTool(Name = "get_person", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(McpPerson))]
    [Description("Reads one person's page: the note, when they were last heard, their newest conversations and facts, and the open tasks owed to them. Give the id or the name, not both.")]
    public async Task<CallToolResult> GetPersonAsync(
        [Description("The person's id (UUID).")] string? id = null,
        [Description("The person's name, in any case.")] string? name = null,
        CancellationToken ct = default)
    {
        if ((id is null) == (name is null))
        {
            throw new McpProtocolException("Give either id or name.", McpErrorCode.InvalidParams);
        }

        Guid? personId = null;
        if (id is not null)
        {
            personId = Guid.TryParse(id, out var parsed)
                ? parsed
                : throw new McpProtocolException("id must be a UUID.", McpErrorCode.InvalidParams);
        }
        else
        {
            personId = (await people.ListAsync(ct)).FirstOrDefault(p => string.Equals(p.Name, name!.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
        }

        if (personId is null || await people.ViewAsync(personId.Value, ct) is not { } view)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "No such person." }] };
        }

        return Ok(new McpPerson(
            view.Id, view.Name, view.Note, view.CreatedAt, view.LastSeenAt, view.Voices, view.Conversations, view.Facts, view.OpenTasks));
    }

    private static CallToolResult Ok<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }
}
