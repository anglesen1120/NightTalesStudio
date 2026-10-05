using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Stories.Approval;

/// <summary>
/// The interim fiction-only approval gate until 02.3.3-T1 supplies fact-sheet and attestation checks.
/// </summary>
/// <remarks>
/// Fiction succeeds without an accepted attestation text version. Non-fiction fails closed at the
/// fact-sheet check; moderation and state checks remain the approve command's responsibility.
/// </remarks>
public sealed class NullStoryApprovalGate : IStoryApprovalGate
{
    private static readonly StoryApprovalGateResult Allowed = new(null);

    /// <inheritdoc />
    public ValueTask<ErrorOr<StoryApprovalGateResult>> ValidateAsync(
        Episode episode,
        SeriesAggregate series,
        StoryVersion version,
        AttestationInput? attestation,
        CancellationToken ct) => ValueTask.FromResult<ErrorOr<StoryApprovalGateResult>>(
            series.Topic.IsFiction()
                ? Allowed
                : Error.Conflict(ErrorCodes.StoryFactSheetOutdated, "A true-crime story needs a current approved fact sheet before approval."));
}
