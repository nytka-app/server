using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace OmiPlatform.Omi.Tests;

public sealed class OmiApiClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static OmiApiClient CreateClient(StubHttp stub, int pageSize = 2, string apiKey = "omi_dev_test")
    {
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.omi.me/v1/dev/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var options = Options.Create(new OmiOptions { ApiKey = apiKey, PageSize = pageSize, BackfillFrom = new DateOnly(2026, 1, 1) });
        return new OmiApiClient(http, options, NullLogger<OmiApiClient>.Instance);
    }

    [Fact]
    public async Task FetchConversationsAsync_pages_until_a_short_page_ends_it()
    {
        var stub = new StubHttp()
            .RespondJson(Fixture("conversations_page1.json"))   // 2 items == pageSize -> keep paging
            .RespondJson(Fixture("conversations_page2.json"));  // 1 item < pageSize -> stop

        var client = CreateClient(stub, pageSize: 2);
        var window = new DateWindow(new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 21));

        var documents = new List<OmiRawDocument>();
        await foreach (var document in client.FetchConversationsAsync(window, CancellationToken.None))
        {
            documents.Add(document);
        }

        Assert.Equal(["conv-1", "conv-2", "conv-3"], documents.Select(d => d.DocId));
        Assert.All(documents, d => Assert.Equal("conversation", d.DocType));
        Assert.Equal(new DateOnly(2026, 9, 20), documents[0].Day);
        Assert.Equal(2, stub.Requests.Count);

        // Two requests: offset=0 then offset=2, both scoped to the window and asking for transcripts.
        Assert.Contains("offset=0", stub.Requests[0].ToString());
        Assert.Contains("offset=2", stub.Requests[1].ToString());
        Assert.Contains("include_transcript=true", stub.Requests[0].ToString());
    }

    [Fact]
    public async Task FetchConversationsAsync_stops_after_one_short_page()
    {
        var stub = new StubHttp().RespondJson(Fixture("conversations_page2.json"));
        var client = CreateClient(stub, pageSize: 25);
        var window = new DateWindow(new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 21));

        var documents = new List<OmiRawDocument>();
        await foreach (var document in client.FetchConversationsAsync(window, CancellationToken.None))
        {
            documents.Add(document);
        }

        Assert.Single(documents);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task FetchMemoriesAsync_carries_no_date_filter()
    {
        var stub = new StubHttp().RespondJson(Fixture("memories_page1.json"));
        var client = CreateClient(stub, pageSize: 25);

        var documents = new List<OmiRawDocument>();
        await foreach (var fetched in client.FetchMemoriesAsync(CancellationToken.None))
        {
            documents.Add(fetched);
        }

        var document = Assert.Single(documents);
        Assert.Equal("memory", document.DocType);
        Assert.Equal("mem-1", document.DocId);
        Assert.Equal(new DateOnly(2026, 9, 20), document.Day);
        Assert.DoesNotContain("start_date", stub.Requests[0].ToString());
    }

    [Fact]
    public async Task Unauthorized_response_is_a_helpful_exception()
    {
        var stub = new StubHttp().Respond(HttpStatusCode.Unauthorized, "{\"detail\":\"Invalid API key\"}");
        var client = CreateClient(stub);

        var exception = await Assert.ThrowsAsync<OmiApiException>(async () =>
        {
            await foreach (var _ in client.FetchMemoriesAsync(CancellationToken.None))
            {
            }
        });

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("Omi__ApiKey", exception.Message);
    }
}
