namespace Nytka.Server;

/// <summary>Bound from the <c>Nytka</c> section: environment variables <c>Nytka__...</c>.</summary>
public sealed class NytkaOptions
{
    public const string Section = "Nytka";

    public string AdminToken { get; set; } = "";

    public SttOptions Stt { get; set; } = new();

    public ConversationOptions Conversations { get; set; } = new();

    public AudioOptions Audio { get; set; } = new();

    public sealed class SttOptions
    {
        public string Url { get; set; } = "";

        public string? ApiKey { get; set; }

        public string? Model { get; set; }

        public string? Language { get; set; }
    }

    public sealed class ConversationOptions
    {
        public TimeSpan Gap { get; set; } = TimeSpan.FromMinutes(2);
    }

    public sealed class AudioOptions
    {
        public int RetentionDays { get; set; } = 14;
    }
}
