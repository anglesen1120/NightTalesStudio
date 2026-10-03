namespace NightTales.Application.Features.Assets.Jobs;

/// <summary>
/// The <c>RetentionSweep</c> configuration section of <see cref="RetentionSweepJob"/> (09.7.3-T1). Every
/// key has a default, so none is a required start-up key.
/// </summary>
public sealed class RetentionSweepOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "RetentionSweep";

    /// <summary>The rows one batch reads; also the page size of the audit scan.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// The most batches one phase runs. A backlog beyond it is reported (<c>capped</c>) and left for
    /// tomorrow, so a run always ends in bounded time.
    /// </summary>
    public int MaxBatches { get; set; } = 20;

    /// <summary>
    /// <see langword="false"/> skips the run and removes the recurring registration on the next Worker
    /// start — the rollback switch.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
