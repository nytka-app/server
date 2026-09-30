using Nytka.Server.Ai;
using Nytka.Server.Api;
using Nytka.Server.Auth;

namespace Nytka.Server.Ask;

/// <summary>
/// <c>POST /api/v1/ask</c> (docs/specs/v0.8.md, Ask). It only reads, so a <c>read</c> token may post to it. Neither
/// the question nor the answer is stored or logged, and every failure message is a fixed sentence.
/// </summary>
public static class AskEndpoints
{
    public const string NotConfigured = "No language model is configured.";
    public const string TimedOut = "The language model did not answer in time.";
    public const string Failed = "The language model could not answer.";

    public sealed record AskRequest(string? Question);

    public static RouteGroupBuilder MapAsk(this RouteGroupBuilder api)
    {
        api.MapPost("/ask", AskAsync).AllowRead(anyMethod: true);
        return api;
    }

    /// <summary>The question trimmed, or null when it is not 1 to 500 characters.</summary>
    public static string? ValidQuestion(string? question) =>
        question?.Trim() is { Length: > 0 and <= AskService.MaxQuestionLength } trimmed ? trimmed : null;

    private static async Task<IResult> AskAsync(HttpRequest http, AskService ask, CancellationToken ct)
    {
        var request = await TokenEndpoints.ReadBodyAsync<AskRequest>(http, ct);
        if (ValidQuestion(request?.Question) is not { } question)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["question"] = [$"Must be 1 to {AskService.MaxQuestionLength} characters."],
            });
        }

        if (!ask.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: NotConfigured);
        }

        try
        {
            return Results.Ok(await ask.AskAsync(question, ct));
        }
        catch (LlmException error)
        {
            return error.TimedOut
                ? Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout, title: TimedOut)
                : Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: Failed);
        }
    }
}
