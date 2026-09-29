using System.Net;

namespace Nytka.Server.Tests.Mcp;

/// <summary>/mcp is never open, whatever its host does or does not do yet.</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpGuardTests(PostgresFixture db)
{
    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public async Task Mcp_without_a_token_is_refused(string method)
    {
        using var server = new NytkaApiFactory(db);

        var response = await server.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), "/mcp"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_with_a_wrong_token_is_refused()
    {
        using var server = new NytkaApiFactory(db);
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", NytkaApiFactory.Token + "x");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/mcp", null)).StatusCode);
    }
}
