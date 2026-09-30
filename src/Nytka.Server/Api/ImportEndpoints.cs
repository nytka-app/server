using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Nytka.Server.Import;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The Omi import (docs/specs/v0.7.md): one file in, once. Admin only.</summary>
public static class ImportEndpoints
{
    public const long MaxBodyBytes = 100L * 1024 * 1024;

    public static RouteGroupBuilder MapImport(this RouteGroupBuilder api)
    {
        api.MapGroup("/import").MapPost("/omi", OmiAsync);
        return api;
    }

    private static async Task<IResult> OmiAsync(
        HttpRequest http, string? overlapping, ImportStore store, TimeProvider time, CancellationToken ct)
    {
        if (overlapping is not (null or "import"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["overlapping"] = ["Must be import, or left out."] });
        }

        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        if (http.ContentLength > MaxBodyBytes)
        {
            return TooLarge();
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(new LimitedStream(http.Body, MaxBodyBytes), cancellationToken: ct);
        }
        catch (JsonException)
        {
            return NotAnExport();
        }
        catch (BodyTooLargeException)
        {
            return TooLarge();
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge();
        }

        using (document)
        {
            var now = time.GetUtcNow();
            try
            {
                return Results.Ok(await store.ImportAsync(OmiExportParser.Parse(document.RootElement, now), overlapping == "import", now, ct));
            }
            catch (OmiExportException)
            {
                return NotAnExport();
            }
        }
    }

    private static IResult NotAnExport() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: new OmiExportException().Message);

    private static IResult TooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The file is larger than 100 MB.");

    private sealed class BodyTooLargeException : Exception;

    /// <summary>Passes <paramref name="inner"/> through and throws once more than <paramref name="limit"/> bytes came out of it.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            _read += count;
            return _read > limit ? throw new BodyTooLargeException() : count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
