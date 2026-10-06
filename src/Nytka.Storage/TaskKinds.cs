namespace Nytka.Storage;

/// <summary>
/// What the model calls each candidate it takes from a conversation (docs/specs/task-kinds.md): a <c>commitment</c> is a task, an
/// <c>idea</c> is kept in <c>tasks</c> under its own kind, <c>advice</c> goes into a note per topic and <c>noise</c> is dropped.
/// An item's owner is the <c>wearer</c> or <c>other</c>: only the wearer's items are kept, except a commitment of someone else, which
/// is kept as a <c>waiting_on</c> task (what they owe the wearer).
/// </summary>
public static class TaskKinds
{
    public const string Commitment = "commitment";
    public const string Idea = "idea";
    public const string Advice = "advice";
    public const string Noise = "noise";

    /// <summary>The stored kind of what someone else promised the wearer; the model never returns it.</summary>
    public const string WaitingOn = "waiting_on";

    public const string Wearer = "wearer";
    public const string Other = "other";

    /// <summary>The kinds a row of <c>tasks</c> may have.</summary>
    public static bool IsStored(string? kind) => kind is Commitment or Idea or WaitingOn;

    /// <summary>The kinds the model may return.</summary>
    public static bool IsKind(string? kind) => kind is Commitment or Idea or Advice or Noise;

    public static bool IsOwner(string? owner) => owner is Wearer or Other;
}
