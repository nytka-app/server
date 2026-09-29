namespace Nytka.Server.Jobs;

/// <summary>What a handler wants next: done (the job is deleted) or another run after a delay.</summary>
public readonly record struct JobOutcome(TimeSpan? RunAgainAfter)
{
    public static JobOutcome Done => default;

    public static JobOutcome RunAgain(TimeSpan delay) => new(delay);
}
