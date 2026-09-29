using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.Webhooks;

/// <summary>
/// One <c>deliver-webhook</c> job: POSTs a delivery's payload, signed, and records the outcome. It counts
/// attempts itself (the job's own counter is for crashes) and reschedules with <see cref="JobOutcome.RunAgain"/>,
/// so a failing receiver never fails the job and only ever holds the Hooks lane for one request of at most
/// <see cref="Timeout"/>.
/// </summary>
public sealed class DeliverWebhookHandler(
    WebhookStore webhooks, IHttpClientFactory httpClients, TimeProvider time, ILogger<DeliverWebhookHandler> logger)
    : IJobHandler
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The wait after the first, second, ... failed attempt; six attempts in all.</summary>
    public static readonly IReadOnlyList<TimeSpan> Retries =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];

    public string Kind => JobKinds.DeliverWebhook;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var deliveryId = JsonSerializer.Deserialize<DeliveryPayload>(job.Payload)!.DeliveryId;
        if (await webhooks.GetPendingAsync(deliveryId, ct) is not { } delivery)
        {
            return JobOutcome.Done;
        }

        var (statusCode, error) = await SendAsync(delivery, ct);
        var attempt = delivery.Attempts + 1;
        if (statusCode is >= 200 and < 300)
        {
            await webhooks.DeliveredAsync(delivery.Id, statusCode.Value, time.GetUtcNow(), ct);
            return JobOutcome.Done;
        }

        if (attempt > Retries.Count)
        {
            logger.LogWarning("Webhook delivery {DeliveryId} failed after {Attempts} attempts ({Error}).", delivery.Id, attempt, error);
            await webhooks.GiveUpAsync(delivery.Id, statusCode, error!, ct);
            return JobOutcome.Done;
        }

        await webhooks.AttemptFailedAsync(delivery.Id, statusCode, error!, ct);
        return JobOutcome.RunAgain(Retries[attempt - 1]);
    }

    /// <summary>Only on a crash-loop of the handler itself; HTTP failures never throw, so this is not an outcome of the receiver.</summary>
    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct)
    {
        var deliveryId = JsonSerializer.Deserialize<DeliveryPayload>(job.Payload)!.DeliveryId;
        return webhooks.GiveUpAsync(deliveryId, null, "internal error", ct);
    }

    private async Task<(int? StatusCode, string? Error)> SendAsync(PendingDelivery delivery, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(delivery.Payload);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("Nytka-Event", delivery.EventType);
        request.Headers.Add("Nytka-Delivery", delivery.Id.ToString());
        request.Headers.Add("Nytka-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("Nytka-Signature", WebhookSigner.Sign(delivery.Secret, timestamp, body));

        try
        {
            using var response = await httpClients.CreateClient(WebhooksExtensions.ClientName).SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            return (status, status is >= 200 and < 300 ? null : $"HTTP {status}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "timeout");
        }
        catch (HttpRequestException error)
        {
            // Never the exception's message: it can carry the URL. A short class of failure only.
            return (null, error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                ? "connection refused"
                : error.HttpRequestError == HttpRequestError.NameResolutionError ? "name not resolved" : "connection error");
        }
    }
}
