using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Server.Auth;
using Nytka.Storage;

namespace Nytka.Server.Tests.Auth;

/// <summary>The scope rules on the real routes: what an <c>admin</c>, a <c>read</c> and no token may call.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopeTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    public static TheoryData<string, string> AdminOnly => new()
    {
        { "GET", "/api/v1/status" },
        { "POST", "/api/v1/chunks" },
        { "GET", "/api/v1/diagnostics" },
        { "POST", "/api/v1/diagnostics" },
        { "GET", "/api/v1/settings" },
        { "PATCH", "/api/v1/settings" },
        { "GET", "/api/v1/tokens" },
        { "POST", "/api/v1/tokens" },
        { "DELETE", "/api/v1/tokens/018f0000-0000-7000-8000-000000000000" },
        { "DELETE", "/api/v1/conversations/018f0000-0000-7000-8000-000000000000" },
        { "GET", "/api/v1/conversations/018f0000-0000-7000-8000-000000000000/transcriptions" },
    };

    [Theory]
    [MemberData(nameof(AdminOnly))]
    public async Task A_read_token_gets_403_where_the_endpoint_needs_admin(string method, string path)
    {
        var response = await _server.CreateClientWithScope("read").SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [MemberData(nameof(AdminOnly))]
    public async Task No_token_gets_401_where_the_endpoint_needs_admin(string method, string path)
    {
        var response = await _server.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("GET", "/api/v1/status")]
    [InlineData("GET", "/api/v1/settings")]
    [InlineData("GET", "/api/v1/tokens")]
    [InlineData("GET", "/api/v1/diagnostics")]
    public async Task A_named_admin_token_may_call_admin_endpoints(string method, string path)
    {
        var response = await _server.CreateClientWithScope("admin").SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_read_token_may_call_info_and_sees_its_scope()
    {
        var response = await _server.CreateClientWithScope("read").GetAsync("/api/v1/info");
        var info = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("read", info.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Info_reports_admin_for_the_environment_token_and_for_a_named_admin_token()
    {
        var environment = await _server.CreateAuthorizedClient().GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/info");
        var named = await _server.CreateClientWithScope("admin").GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/info");

        Assert.Equal("admin", environment.GetProperty("scope").GetString());
        Assert.Equal("admin", named.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task The_environment_token_works_on_an_empty_database()
    {
        await db.ExecuteAsync("truncate api_tokens, settings");

        var client = _server.CreateAuthorizedClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/info")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/tokens")).StatusCode);
    }

    [Fact]
    public async Task The_environment_token_is_never_listed()
    {
        var client = _server.CreateAuthorizedClient();

        var listed = await client.GetStringAsync("/api/v1/tokens");

        Assert.DoesNotContain(NytkaApiFactory.Token, listed, StringComparison.Ordinal);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from api_tokens"));
    }

    [Fact]
    public async Task A_revoked_token_gets_401_on_its_next_request()
    {
        var client = _server.CreateClientWithScope("read");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/info")).StatusCode);

        await db.ExecuteAsync("update api_tokens set revoked_at = now()");
        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic dGVzdDp0ZXN0")]
    [InlineData("Bearer nyt_unknown")]
    [InlineData("Bearer test-admin-token-0123456789-abcdefghi")]
    public async Task A_missing_malformed_or_unknown_token_gets_401(string? header)
    {
        var client = _server.CreateClient();
        if (header is not null)
        {
            // TryAddWithoutValidation: "Bearer" alone is not a valid header value to construct.
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        }

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_scheme_name_is_case_insensitive()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", NytkaApiFactory.Token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/info")).StatusCode);
    }

    [Fact]
    public async Task Healthz_stays_open_even_with_a_bad_token()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Mcp_without_a_token_or_with_a_revoked_one_gets_401()
    {
        var revoked = _server.CreateClientWithScope("read");
        await db.ExecuteAsync("update api_tokens set revoked_at = now()");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _server.CreateClient().PostAsync("/mcp", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.PostAsync("/mcp", null)).StatusCode);
    }
}

/// <summary>
/// The seam other tracks build on, on a host of its own: <c>RequireNytkaAuth</c> and <c>AllowRead</c> on a
/// route and on a group, and a route that has neither.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopeSeamTests(PostgresFixture db) : IAsyncLifetime
{
    private WebApplication _app = null!;

    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOptions<NytkaOptions>().Configure(o => o.AdminToken = NytkaApiFactory.Token);
        builder.Services.AddSingleton(db.DataSource);
        builder.Services.AddSingleton<TokenStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddProblemDetails();
        builder.Services.AddNytkaAuth();

        _app = builder.Build();
        _app.UseNytkaAuth();
        _app.MapGet("/unmarked", () => "unmarked");
        _app.MapGet("/anon", () => "anon").AllowAnonymous();
        _app.MapGet("/healthz", () => "healthy");
        _app.MapMethods("/rw", ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"], () => "rw").RequireNytkaAuth().AllowRead();
        _app.MapPost("/rpc", () => "rpc").RequireNytkaAuth().AllowRead(anyMethod: true);
        _app.MapGet("/admin", () => "admin").RequireNytkaAuth();
        _app.MapGet("/read", () => "read").RequireNytkaAuth().AllowRead();
        var closed = _app.MapGroup("/closed").RequireNytkaAuth();
        closed.MapGet("/admin", () => "admin");
        closed.MapGet("/read", () => "read").AllowRead();
        _app.MapGroup("/readable").RequireNytkaAuth().AllowRead().MapGet("/any", () => "read");
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<string> NewToken(string scope)
    {
        var token = TokenSecret.Generate();
        await new TokenStore(db.DataSource).CreateAsync(
            Guid.CreateVersion7(), $"{scope}-{Guid.NewGuid():N}", scope, TokenSecret.Hash(token), TokenSecret.Hint(token), DateTimeOffset.UtcNow, default);
        return token;
    }

    private async Task<HttpStatusCode> Get(string path, string? token, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return (await _client.SendAsync(request)).StatusCode;
    }

    [Theory]
    [InlineData("/admin", "admin", HttpStatusCode.OK)]
    [InlineData("/admin", "read", HttpStatusCode.Forbidden)]
    [InlineData("/read", "admin", HttpStatusCode.OK)]
    [InlineData("/read", "read", HttpStatusCode.OK)]
    [InlineData("/closed/admin", "admin", HttpStatusCode.OK)]
    [InlineData("/closed/admin", "read", HttpStatusCode.Forbidden)]
    [InlineData("/closed/read", "admin", HttpStatusCode.OK)]
    [InlineData("/closed/read", "read", HttpStatusCode.OK)]
    [InlineData("/readable/any", "admin", HttpStatusCode.OK)]
    [InlineData("/readable/any", "read", HttpStatusCode.OK)]
    public async Task A_route_is_closed_to_read_unless_it_or_its_group_allows_read(string path, string scope, HttpStatusCode expected) =>
        Assert.Equal(expected, await Get(path, await NewToken(scope)));

    [Theory]
    [InlineData("/admin")]
    [InlineData("/read")]
    [InlineData("/closed/admin")]
    [InlineData("/closed/read")]
    [InlineData("/readable/any")]
    public async Task A_guarded_route_needs_a_token(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await Get(path, null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get(path, "nyt_unknown"));
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/read")]
    [InlineData("/closed/admin")]
    [InlineData("/readable/any")]
    public async Task The_environment_token_is_admin_everywhere(string path) =>
        Assert.Equal(HttpStatusCode.OK, await Get(path, NytkaApiFactory.Token));

    [Theory]
    [InlineData("GET", HttpStatusCode.OK)]
    [InlineData("HEAD", HttpStatusCode.OK)]
    [InlineData("POST", HttpStatusCode.Forbidden)]
    [InlineData("PUT", HttpStatusCode.Forbidden)]
    [InlineData("PATCH", HttpStatusCode.Forbidden)]
    [InlineData("DELETE", HttpStatusCode.Forbidden)]
    public async Task AllowRead_admits_a_read_token_on_GET_and_HEAD_only(string method, HttpStatusCode expected)
    {
        Assert.Equal(expected, await Get("/rw", await NewToken("read"), new HttpMethod(method)));
        Assert.Equal(HttpStatusCode.OK, await Get("/rw", await NewToken("admin"), new HttpMethod(method)));
    }

    [Fact]
    public async Task AllowRead_for_any_method_admits_a_read_token_to_a_POST() =>
        Assert.Equal(HttpStatusCode.OK, await Get("/rpc", await NewToken("read"), HttpMethod.Post));

    [Fact]
    public async Task A_newly_mapped_route_without_the_hook_still_needs_an_admin_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await Get("/unmarked", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get("/unmarked", "nyt_unknown"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("/unmarked", await NewToken("read")));
        Assert.Equal(HttpStatusCode.OK, await Get("/unmarked", await NewToken("admin")));
        Assert.Equal(HttpStatusCode.OK, await Get("/unmarked", NytkaApiFactory.Token));
    }

    [Fact]
    public async Task Healthz_and_an_AllowAnonymous_route_need_no_token()
    {
        Assert.Equal(HttpStatusCode.OK, await Get("/healthz", null));
        Assert.Equal(HttpStatusCode.OK, await Get("/anon", null));
    }
}
