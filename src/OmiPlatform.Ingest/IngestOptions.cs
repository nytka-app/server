namespace OmiPlatform.Ingest;

public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>How often the reconcile cycle runs. Omi has no documented "lands the next
    /// morning" lag the way Oura does, so this can be short — the promise made to set this stack
    /// up was a few-minute lag, not hours.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Wait between the first failure and the first retry of a whole cycle.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Apply pending migrations at startup. Safe with a single instance, which is the
    /// documented deployment shape.</summary>
    public bool MigrateOnStartup { get; set; } = true;
}
