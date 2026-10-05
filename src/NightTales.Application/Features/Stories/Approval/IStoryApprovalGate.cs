using ErrorOr;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Stories.Approval;

/// <summary>
/// Validates the checkpoint-one requirements before a story version is approved.
/// </summary>
public interface IStoryApprovalGate
{
    /// <summary>
    /// Checks the episode, series, story version and optional attestation for approval.
    /// </summary>
    ValueTask<ErrorOr<StoryApprovalGateResult>> ValidateAsync(
        Episode episode,
        SeriesAggregate series,
        StoryVersion version,
        AttestationInput? attestation,
        CancellationToken ct);
}
