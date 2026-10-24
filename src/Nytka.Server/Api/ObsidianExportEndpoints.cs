using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Nytka.Server.Privacy;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The Obsidian export (docs/specs/export.md, Obsidian export): a ZIP of Markdown notes for an Obsidian vault, one
/// per conversation, one per person, and a <c>Tasks.md</c> for tasks added by hand with no conversation (left out
/// when there are none). Admin only.
/// </summary>
public static class ObsidianExportEndpoints
{
    public const string ContentType = "application/zip";

    public static RouteGroupBuilder MapObsidianExport(this RouteGroupBuilder api)
    {
        api.MapGet("/export/obsidian", Export);
        return api;
    }

    private static IResult Export(ObsidianExportStore export, TimeProvider time, SensitivityScope sensitivity, HttpContext http)
    {
        var now = time.GetUtcNow();
        var hidden = sensitivity.ExportHidden;
        return Results.Stream(async stream =>
        {
            var ct = http.RequestAborted;
            // ZipArchive flushes a local header and the central directory with synchronous Stream.Write calls
            // (on entry close and on its own Dispose); Kestrel's response body disallows those unless asked.
            var bodyControl = http.Features.Get<IHttpBodyControlFeature>();
            if (bodyControl is not null)
            {
                bodyControl.AllowSynchronousIO = true;
            }

            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

            await foreach (var entry in export.ReadAsync(ct, hidden))
            {
                var (name, content) = entry switch
                {
                    ObsidianPersonEntry p => (ObsidianMarkdown.PersonFileName(p.Person), ObsidianMarkdown.Person(p.Person)),
                    ObsidianConversationEntry c => (ObsidianMarkdown.ConversationFileName(c.Conversation), ObsidianMarkdown.Conversation(c.Conversation)),
                    ObsidianTasksEntry t => (ObsidianMarkdown.TasksFileName, ObsidianMarkdown.Tasks(t.Tasks)),
                    _ => throw new InvalidOperationException($"Unknown Obsidian export entry: {entry.GetType()}"),
                };
                await WriteAsync(zip, name, content, ct);
            }
        }, ContentType, $"nytka-obsidian-{now:yyyyMMdd-HHmmss}.zip");
    }

    private static async Task WriteAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var zipEntry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var entryStream = zipEntry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await entryStream.WriteAsync(bytes, ct);
    }
}
