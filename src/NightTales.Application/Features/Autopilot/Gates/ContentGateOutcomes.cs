using ErrorOr;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The one mapping from the content gate's verdict to what the Domain's check 7 reads (02.8.2-T2): the gate speaks
/// <see cref="ContentGateVerdict"/> (thresholds, scores, flagged segments), the rule speaks
/// <see cref="ModerationGateOutcome"/> (a flag and the one detail code the owner is told about).
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail closed, on every path.</b> There is no member here that can answer "pass" for something the gate could
/// not check: the two <see cref="FailClosed(string?)"/> shapes are how a missing style template, unreadable
/// content and an error from the gate itself arrive at <c>moderation_gate</c>, and all three set
/// <see cref="ModerationGateOutcome.Flagged"/>.
/// </para>
/// <para>
/// <b>The detail code is a code.</b> <see cref="ModerationGateOutcome.UnavailableDetail"/> carries
/// <see cref="GateDetailCodes.ModeratorUnavailable"/> or <see cref="GateDetailCodes.FieldAbsent"/> — the closed set
/// of <see cref="GateDetailCodes.All"/> — and never a message, a category or a fragment of text (D19). The
/// evaluator's stop reason reads the moderator one, so an outage still tells the owner "the moderator could not be
/// reached" instead of "checks failed".
/// </para>
/// </remarks>
public static class ContentGateOutcomes
{
    /// <summary>Projects a verdict the gate produced into the check's outcome.</summary>
    /// <param name="verdict">The verdict, pass or fail.</param>
    /// <returns>The outcome, with the verdict's own detail code when it carries one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="verdict"/> is <see langword="null"/>.</exception>
    public static ModerationGateOutcome From(ContentGateVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return new ModerationGateOutcome(verdict.Flagged, verdict.UnavailableDetail);
    }

    /// <summary>
    /// The outcome of a check 7 that could not run: always flagged, with a closed detail code.
    /// </summary>
    /// <param name="detailCode">
    /// The code the check failed with, or <see langword="null"/> to fail with
    /// <see cref="GateDetailCodes.FieldAbsent"/>. A code outside <see cref="GateDetailCodes.All"/> is replaced by
    /// <see cref="GateDetailCodes.FieldAbsent"/>, so a detail the run log cannot explain is never stored.
    /// </param>
    /// <returns>The fail-closed outcome.</returns>
    public static ModerationGateOutcome FailClosed(string? detailCode) =>
        new(true, KnownCode(detailCode));

    /// <summary>
    /// The outcome of a check 7 whose gate answered an error: always flagged, and the error's code kept only when it
    /// is one of the gate's own detail codes (an <see cref="GateDetailCodes.ModeratorUnavailable"/> error keeps the
    /// stop reason; anything else fails as <see cref="GateDetailCodes.FieldAbsent"/>).
    /// </summary>
    /// <param name="error">The error the gate answered.</param>
    /// <returns>The fail-closed outcome.</returns>
    public static ModerationGateOutcome FailClosed(Error error) => new(true, KnownCode(error.Code));

    private static string KnownCode(string? code) =>
        code is not null && GateDetailCodes.All.Contains(code, StringComparer.Ordinal)
            ? code
            : GateDetailCodes.FieldAbsent;
}
