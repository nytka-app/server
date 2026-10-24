using Microsoft.Extensions.Options;
using Nytka.Storage;

namespace Nytka.Server.Search;

/// <summary>
/// Fills the vectors in the background (NYTKA-66), never inside a request. Once an embeddings endpoint is set it
/// learns the vector size from one probe, runs <c>nytka_embeddings_setup</c> (which drops the vectors of another model),
/// then embeds the rows still marked to do, oldest id first, a batch per kind and request. The marks are the cursor: a
/// restart, a failed request or an edited text resumes exactly where it stopped. Without pgvector it says so once and
/// waits for the settings to change.
/// </summary>
public sealed partial class EmbeddingIndexer(
    EmbeddingStore store,
    IEmbeddingsClient client,
    IOptionsMonitor<EmbeddingsOptions> options,
    EmbeddingState state,
    ILogger<EmbeddingIndexer> logger) : BackgroundService
{
    /// <summary>A line shorter than this ("Так.", "okay") gets no vector: alone it means nothing.</summary>
    public const int MinSegmentLength = 12;

    /// <summary>Characters of one text sent to the model; a longer summary is cut.</summary>
    public const int MaxTextLength = 4000;

    public const string Probe = "dimension probe";

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private (string? BaseUrl, string? Model, int? Dimensions)? _setUp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOnceAsync(stoppingToken) == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The endpoint or the tables may have changed under it: set up again before the next try.
                _setUp = null;
                state.Clear();
                if (ex is EmbeddingsException embeddings)
                {
                    LogEndpointFailed(logger, embeddings.Message);
                }
                else
                {
                    LogFailed(logger, ex);
                }

                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One step: set up when the settings changed, then one batch of each kind. Returns how many rows it marked done.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var embeddings = options.CurrentValue;
        if (!embeddings.IsConfigured)
        {
            if (_setUp is not null)
            {
                LogOff(logger);
            }

            _setUp = null;
            state.Clear();
            return 0;
        }

        var wanted = (embeddings.BaseUrl, embeddings.Model, embeddings.Dimensions);
        if (_setUp != wanted)
        {
            state.Clear();
            var probe = await client.EmbedAsync([Probe], ct);
            var dimensions = probe[0].Length;
            if (embeddings.Dimensions is { } asked && asked != dimensions)
            {
                throw new EmbeddingsException("The embeddings endpoint answered vectors of another size than embeddings.dimensions.");
            }

            if (dimensions > EmbeddingsSettings.MaxDimensions)
            {
                throw new EmbeddingsException("The model's vectors are larger than 2000 dimensions; set embeddings.dimensions.");
            }

            var answer = await store.SetupAsync(embeddings.Model!, dimensions, ct);
            _setUp = wanted;
            if (answer != EmbeddingStore.Ready)
            {
                LogUnavailable(logger);
                return 0;
            }

            state.Set(embeddings.Model!, dimensions);
            LogReady(logger, dimensions);
        }

        if (state.Current is not { } ready)
        {
            return 0;
        }

        var done = 0;
        foreach (var kind in EmbeddingStore.Kinds)
        {
            var rows = await store.PendingAsync(kind, Math.Max(1, embeddings.BatchSize), ct);
            if (rows.Count == 0)
            {
                continue;
            }

            var texts = rows.Select(r => Input(kind, r.Text)).ToList();
            var wantedTexts = texts.Where(t => t is not null).Select(t => t!).ToList();
            var answers = await client.EmbedAsync(wantedTexts, ct);
            if (answers.Any(v => v.Length != ready.Dimensions))
            {
                throw new EmbeddingsException("The embeddings endpoint changed its vector size.");
            }

            var next = 0;
            var vectors = texts.Select(t => t is null ? null : answers[next++]).ToList();
            done += await store.SaveAsync(kind, rows, vectors, ct);
        }

        return done;
    }

    /// <summary>What goes to the model for a row, or null when it gets no vector.</summary>
    private static string? Input(EmbeddingStore.Kind kind, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || (kind == EmbeddingStore.Segments && text.Trim().Length < MinSegmentLength))
        {
            return null;
        }

        return text.Length > MaxTextLength ? text[..MaxTextLength] : text;
    }

    [LoggerMessage(LogLevel.Information, "Embeddings: vectors of {Dimensions} dimensions are ready; search and Ask also match by meaning.")]
    private static partial void LogReady(ILogger logger, int dimensions);

    [LoggerMessage(LogLevel.Warning, "Embeddings: pgvector is not available in this Postgres (see docs/guide/configuration.md, Vector search); search stays full-text.")]
    private static partial void LogUnavailable(ILogger logger);

    [LoggerMessage(LogLevel.Information, "Embeddings: no endpoint is set; search is full-text only.")]
    private static partial void LogOff(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Embeddings: {Reason} Will retry.")]
    private static partial void LogEndpointFailed(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Error, "Embeddings: indexing failed; will retry.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
