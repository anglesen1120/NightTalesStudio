namespace NightTales.Application.Abstractions.Usage;

/// <summary>
/// One provider call to meter (02 README D14, 12 CR-17): what <see cref="IUsageMeter"/> writes as one
/// <c>usage_records</c> row.
/// </summary>
/// <param name="UserId">The owner of the run; the row is tenant-owned (09 D10).</param>
/// <param name="Provider">The provider, for example <c>openai</c>.</param>
/// <param name="Operation">The D14 operation (<c>story</c>, <c>story_review</c>, <c>story_revision</c>, <c>fact_sheet</c>,
/// <c>bible_proposal</c>, <c>idea</c>, <c>source_find</c>, <c>moderation</c>), snake_case.</param>
/// <param name="Model">The model that served the call.</param>
/// <param name="InputTokens">Reported input tokens; <c>null</c> when the response carried no usage.</param>
/// <param name="CachedInputTokens">Reported cache-hit input tokens.</param>
/// <param name="CacheWriteTokens">Reported cache-write tokens; priced, not stored (no column).</param>
/// <param name="OutputTokens">Reported output tokens, reasoning included (O4).</param>
/// <param name="ReservationId">The credit reservation the call settles against.</param>
/// <param name="EpisodeId">The episode the call belongs to, or <c>null</c> for a series-level call.</param>
/// <param name="Estimated"><c>true</c> when the provider reported no usage, so <paramref name="CostUsd"/> is the D1 bound.</param>
/// <param name="CostUsd">The cost; required when <paramref name="Estimated"/> (the bound), otherwise optional: when
/// <c>null</c> the meter prices the tokens at <paramref name="PriceVersion"/>.</param>
/// <param name="PriceVersion">The run's price version (text, 12 CR-17).</param>
/// <param name="ProviderRequestId">The provider's request id (O8).</param>
/// <param name="SeriesId">The series of the run, when known.</param>
/// <param name="GenerationRunId">The <c>generation_runs</c> row of the call, when known.</param>
public sealed record UsageItem(
    Guid UserId,
    string Provider,
    string Operation,
    string Model,
    int? InputTokens,
    int? CachedInputTokens,
    int? CacheWriteTokens,
    int? OutputTokens,
    Guid? ReservationId,
    Guid? EpisodeId,
    bool Estimated,
    decimal? CostUsd,
    string PriceVersion,
    string? ProviderRequestId,
    Guid? SeriesId = null,
    Guid? GenerationRunId = null);
