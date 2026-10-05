using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>The episode's latest generation run (02 CR-7); no internal error detail is exposed.</summary>
/// <param name="Kind">The generation kind, snake_case on the wire.</param>
/// <param name="Status">The run lifecycle status, snake_case on the wire.</param>
/// <param name="Stage">The D21 stage, snake_case on the wire.</param>
/// <param name="ErrorCode">The safe outcome code, or null.</param>
/// <param name="StartedAt">When the job started, or null while queued.</param>
public sealed record GenerationStatusDto(
    GenerationKind Kind, GenerationRunStatus Status, GenerationStage Stage,
    string? ErrorCode, DateTimeOffset? StartedAt);
