namespace Nytka.Server.Transcription;

/// <summary>A segment in seconds from the start of the WAV that was sent. <paramref name="Speaker"/> is null when the provider named none.</summary>
public sealed record TranscribedSegment(double Start, double End, string Text, string? Speaker = null);

public sealed record TranscriptionResult(string Text, IReadOnlyList<TranscribedSegment> Segments, string RawJson);
