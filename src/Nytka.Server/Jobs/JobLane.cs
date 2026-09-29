namespace Nytka.Server.Jobs;

/// <summary>
/// A share of the job queue with a runner of its own, so a slow job in one lane never delays the
/// others. A job's kind decides its lane (<see cref="JobKinds.LaneOf"/>); the <c>jobs</c> table has
/// no lane column.
/// </summary>
public enum JobLane
{
    /// <summary>
    /// v0.1's kinds, and any kind nobody has claimed. Silero is not thread-safe and transcription
    /// requests go out one at a time, so this lane never runs two jobs at once.
    /// </summary>
    Audio,

    /// <summary>Calls to the language model, which can take minutes.</summary>
    Ai,

    /// <summary>Outgoing webhook deliveries.</summary>
    Hooks,
}
