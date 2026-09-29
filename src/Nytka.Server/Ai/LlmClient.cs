using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nytka.Server.Ai;

/// <summary>
/// Talks to an OpenAI-compatible chat endpoint (<c>POST {baseUrl}/chat/completions</c>). It sends no
/// temperature and no token limit, because some models refuse them. Options are read on every call.
/// </summary>
public sealed class LlmClient(HttpClient http, IOptionsMonitor<LlmOptions> options) : ILlmClient
{
    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct)
    {
        var llm = options.CurrentValue;
        if (!llm.IsConfigured)
        {
            throw new LlmException("The language model is not configured.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, llm.BaseUrl!.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(Body(llm, request), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(llm.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", llm.ApiKey);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(llm.TimeoutSeconds));
        try
        {
            using var response = await http.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException(
                    $"The language model endpoint answered {(int)response.StatusCode}.", (int)response.StatusCode);
            }

            return Content(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException("The language model endpoint did not answer in time.");
        }
        catch (HttpRequestException)
        {
            // Its message can hold the URL; the fixed sentence is all a log or ai_message gets.
            throw new LlmException("The language model endpoint could not be reached.");
        }
    }

    /// <summary>
    /// The request body. In <see cref="LlmOptions.SchemaMode"/> the schema travels as a strict
    /// <c>json_schema</c>; in <see cref="LlmOptions.ObjectMode"/> the endpoint only promises JSON, so the schema goes into the prompt.
    /// </summary>
    public static string Body(LlmOptions llm, LlmRequest request)
    {
        var system = request.System;
        object format;
        if (string.Equals(llm.JsonMode, LlmOptions.ObjectMode, StringComparison.OrdinalIgnoreCase))
        {
            system += "\n\nAnswer with one JSON object that follows this JSON schema, and nothing else:\n" + request.SchemaJson;
            format = new { type = "json_object" };
        }
        else
        {
            format = new
            {
                type = "json_schema",
                json_schema = new { name = request.SchemaName, strict = true, schema = JsonSerializer.Deserialize<JsonElement>(request.SchemaJson) },
            };
        }

        return JsonSerializer.Serialize(new
        {
            model = llm.Model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = request.User },
            },
            response_format = format,
        });
    }

    /// <summary>The assistant's message of the first choice. The error never quotes the answer.</summary>
    private static string Content(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
            // Falls through to the same sentence: what was wrong with the answer is not worth a log line.
        }

        throw new LlmException("The language model endpoint answered without a message.");
    }
}
