namespace Nytka.Server.Transcription;

/// <summary>
/// A segment in seconds from the start of the WAV that was sent. <paramref name="Speaker"/> is null when the provider named none;
/// <paramref name="SpeakerId"/> is the provider's stable id for the voice, and <paramref name="IsUser"/> whether it is the wearer's.
/// </summary>
public sealed record TranscribedSegment(
    double Start, double End, string Text, string? Speaker = null, string? SpeakerId = null, bool? IsUser = null);

public sealed record TranscriptionResult(string Text, IReadOnlyList<TranscribedSegment> Segments, string RawJson);
