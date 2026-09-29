namespace Nytka.Server.Transcription;

/// <summary>A segment in seconds from the start of the WAV that was sent.</summary>
public sealed record TranscribedSegment(double Start, double End, string Text);

public sealed record TranscriptionResult(string Text, IReadOnlyList<TranscribedSegment> Segments, string RawJson);
