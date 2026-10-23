using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nytka.Server.Search;

/// <summary>Talks to <c>POST {baseUrl}/embeddings</c> (OpenAI, Text Embeddings Inference, Ollama, LiteLLM). Options are read on every call.</summary>
public sealed class EmbeddingsClient(HttpClient http, IOptionsMonitor<EmbeddingsOptions> options) : IEmbeddingsClient
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var embeddings = options.CurrentValue;
        if (!embeddings.IsConfigured)
        {
            throw new EmbeddingsException("The embeddings endpoint is not configured.");
        }

        if (inputs.Count == 0)
        {
            return [];
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, embeddings.BaseUrl!.TrimEnd('/') + "/embeddings")
            {
                Content = new StringContent(Body(embeddings, inputs), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(embeddings.ApiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", embeddings.ApiKey);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(embeddings.TimeoutSeconds));
            using var response = await http.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new EmbeddingsException(
                    $"The embeddings endpoint answered {(int)response.StatusCode}.", (int)response.StatusCode);
            }

            return Vectors(await response.Content.ReadAsStringAsync(timeout.Token), inputs.Count);
        }
        catch (EmbeddingsException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmbeddingsException("The embeddings endpoint did not answer in time.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The message of a refused connection can hold the URL; the fixed sentence is all a log gets.
            throw new EmbeddingsException("The embeddings endpoint could not be reached.");
        }
    }

    public static string Body(EmbeddingsOptions embeddings, IReadOnlyList<string> inputs) =>
        embeddings.Dimensions is { } dimensions
            ? JsonSerializer.Serialize(new { model = embeddings.Model, input = inputs, dimensions })
            : JsonSerializer.Serialize(new { model = embeddings.Model, input = inputs });

    /// <summary><c>data[].embedding</c> put in input order by <c>index</c>; all the same size, none empty.</summary>
    private static float[][] Vectors(string raw, int count)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() == count)
            {
                var vectors = new float[count][];
                var position = 0;
                foreach (var item in data.EnumerateArray())
                {
                    var index = item.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : position;
                    if (index < 0 || index >= count || vectors[index] is not null
                        || !item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
                    {
                        break;
                    }

                    vectors[index] = embedding.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                    position++;
                }

                if (position == count && vectors[0].Length > 0 && vectors.All(v => v.Length == vectors[0].Length))
                {
                    return vectors;
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            // Falls through to the same sentence.
        }

        throw new EmbeddingsException("The embeddings endpoint answered without usable vectors.");
    }
}
