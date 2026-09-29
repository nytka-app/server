namespace Nytka.Server.Transcription;

/// <summary>
/// The endpoint failed. The message never holds the response body: an error body can echo the
/// transcript, and this message ends up in the logs and in <c>transcription_batches.error</c>.
/// </summary>
public sealed class TranscriptionException(string message) : Exception(message);
