using NightTales.Domain.Common;

namespace NightTales.Domain.Usage;

/// <summary>
/// One metered provider call (00 overview §6 <c>usage_records</c>). Append-only: the cost of a past call
/// must stay resolvable, so there is no update path — a re-price inserts a new row.
/// </summary>
public sealed class UsageRecord : Entity, IUserOwned, ICreatedAt
{
    private UsageRecord()
    {
    }

    public Guid UserId { get; private set; }

    public Guid? EpisodeId { get; private set; }

    public Guid? SeriesId { get; private set; }

    /// <summary>Placeholder FK to <c>generation_runs</c>, whose constraint 02.2.2-T1 adds.</summary>
    public Guid? GenerationRunId { get; private set; }

    /// <summary>Placeholder FK to <c>render_jobs</c>, whose constraint 09.6.1 adds.</summary>
    public Guid? RenderJobId { get; private set; }

    public Guid? ReservationId { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public UsageOperation Operation { get; private set; }

    public string? Model { get; private set; }

    public int InputTokens { get; private set; }

    public int CachedInputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public int ImageCount { get; private set; }

    public decimal GpuSeconds { get; private set; }

    public bool Estimated { get; private set; }

    public decimal CostUsd { get; private set; }

    public int? PriceVersion { get; private set; }

    public string? ProviderRequestId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Records one metered provider call (02.2.4-T1's <c>UsageMeter</c> is the only writer). Every
    /// optional value defaults to "not measured", so a call writes only what it knows.
    /// </summary>
    public static UsageRecord Record(
        Guid userId,
        UsageOperation operation,
        string provider,
        decimal costUsd,
        DateTimeOffset now,
        Guid? episodeId = null,
        Guid? seriesId = null,
        Guid? generationRunId = null,
        Guid? renderJobId = null,
        Guid? reservationId = null,
        string? model = null,
        int inputTokens = 0,
        int cachedInputTokens = 0,
        int outputTokens = 0,
        int imageCount = 0,
        decimal gpuSeconds = 0m,
        bool estimated = false,
        int? priceVersion = null,
        string? providerRequestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        return new UsageRecord
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EpisodeId = episodeId,
            SeriesId = seriesId,
            GenerationRunId = generationRunId,
            RenderJobId = renderJobId,
            ReservationId = reservationId,
            Provider = provider,
            Operation = operation,
            Model = model,
            InputTokens = inputTokens,
            CachedInputTokens = cachedInputTokens,
            OutputTokens = outputTokens,
            ImageCount = imageCount,
            GpuSeconds = gpuSeconds,
            Estimated = estimated,
            CostUsd = costUsd,
            PriceVersion = priceVersion,
            ProviderRequestId = providerRequestId,
            CreatedAt = now,
        };
    }
}
