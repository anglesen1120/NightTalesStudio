using ErrorOr;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Stories.Approval;

/// <summary>
/// The explicitly permitted approval gate for 02.3.2-T1 until 02.3.3-T1 supplies the real gate.
/// </summary>
/// <remarks>
/// This gate succeeds without an accepted attestation text version. Moderation and state checks
/// remain the approve command's responsibility.
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
        CancellationToken ct) => ValueTask.FromResult<ErrorOr<StoryApprovalGateResult>>(Allowed);
}
