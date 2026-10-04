using System.Net.Sockets;
using System.Text;

namespace Nytka.Server.Calendar;

/// <summary>Why a fetch failed, in a fixed sentence: an exception's own message can carry the feed's URL, so none is passed on.</summary>
public sealed class CalendarFeedException(string message) : Exception(message);

/// <summary>
/// Fetches the ICS feed the way webhooks are delivered: <c>http</c> or <c>https</c>, no redirect (a 3xx fails the fetch), 10 seconds
/// for the whole read, and at most <see cref="MaxBytes"/> of body. Private addresses are allowed, since only the admin sets the URL.
/// </summary>
public sealed class CalendarFeed(IHttpClientFactory httpClients)
{
    public const string ClientName = "nytka-calendar";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public const int MaxBytes = 5 * 1024 * 1024;

    /// <exception cref="CalendarFeedException">Always with a fixed sentence, never the URL.</exception>
    public async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        try
        {
            using var response = await httpClients.CreateClient(ClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new CalendarFeedException($"The calendar feed answered HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new CalendarFeedException("The calendar feed is larger than 5 MB.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = await body.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    throw new CalendarFeedException("The calendar feed is larger than 5 MB.");
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CalendarFeedException("The calendar feed did not answer in time.");
        }
        catch (HttpRequestException error)
        {
            throw new CalendarFeedException(error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                ? "The calendar feed refused the connection."
                : error.HttpRequestError == HttpRequestError.NameResolutionError
                    ? "The calendar feed's host was not found."
                    : "The calendar feed could not be reached.");
        }
        catch (InvalidOperationException)
        {
            // An unusable URL (not absolute); the exception's message would repeat it.
            throw new CalendarFeedException("The calendar feed URL is not usable.");
        }
    }
}
