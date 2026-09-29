using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nytka.Server.Tests.Webhooks;

/// <summary>A local HTTP server standing in for a webhook receiver: records every request and answers a settable status.</summary>
public sealed class TestReceiver : IAsyncDisposable
{
    public sealed record Received(IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private readonly WebApplication _app;

    private TestReceiver(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public ConcurrentQueue<Received> Requests { get; } = new();

    /// <summary>The status to answer; a redirect status also sends a <c>Location</c> pointing at <c>/elsewhere</c>.</summary>
    public int Status { get; set; } = 200;

    public static async Task<TestReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        TestReceiver? receiver = null;
        app.Map("/{**path}", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            receiver!.Requests.Enqueue(new Received(
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body.ToArray()));
            context.Response.StatusCode = receiver.Status;
            if (receiver.Status is >= 300 and < 400)
            {
                context.Response.Headers.Location = "/elsewhere";
            }
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        receiver = new TestReceiver(app, url.TrimEnd('/') + "/hook");
        return receiver;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
