namespace Nytka.Storage;

/// <summary>
/// What the model calls each candidate it takes from a conversation (docs/specs/task-kinds.md): a <c>commitment</c> is a task, an
/// <c>idea</c> is kept in <c>tasks</c> under its own kind, <c>advice</c> goes into a note per topic and <c>noise</c> is dropped.
/// An item's owner is the <c>wearer</c> or <c>other</c>: only the wearer's items are kept.
/// </summary>
public static class TaskKinds
{
    public const string Commitment = "commitment";
    public const string Idea = "idea";
    public const string Advice = "advice";
    public const string Noise = "noise";

    public const string Wearer = "wearer";
    public const string Other = "other";

    /// <summary>The kinds a row of <c>tasks</c> may have.</summary>
    public static bool IsStored(string? kind) => kind is Commitment or Idea;

    /// <summary>The kinds the model may return.</summary>
    public static bool IsKind(string? kind) => kind is Commitment or Idea or Advice or Noise;

    public static bool IsOwner(string? owner) => owner is Wearer or Other;
}
