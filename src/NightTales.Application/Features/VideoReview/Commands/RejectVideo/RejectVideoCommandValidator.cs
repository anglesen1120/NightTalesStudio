using FluentValidation;

namespace NightTales.Application.Features.VideoReview.Commands.RejectVideo;

/// <summary>
/// Validates the rejection's shape — a bounded, non-blank reason and a known target — run by the D4
/// <c>ValidationBehavior</c> before the handler; the state and <c>If-Match</c> preconditions belong to the
/// handler's ordered checks.
/// </summary>
/// <remarks>
/// The reason is judged after trimming because that is what <c>Episode.RejectVideo</c> stores: a
/// whitespace-only reason would otherwise reach the aggregate and be refused there, which answers the same 400
/// without telling the client which field to fix. A NUL (U+0000) is refused here as well as in the aggregate,
/// because PostgreSQL rejects it in a <c>varchar</c> and the alternative is a raw 500; every other character
/// the owner typed is accepted.
/// </remarks>
public sealed class RejectVideoCommandValidator : AbstractValidator<RejectVideoCommand>
{
    /// <summary>The longest reason <c>episodes.video_reject_reason</c> stores, after trimming.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Creates the validator.</summary>
    public RejectVideoCommandValidator()
    {
        RuleFor(command => command.EpisodeId).NotEmpty();
        RuleFor(command => command.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= MaxReasonLength)
            .WithMessage($"Reason must be 1 to {MaxReasonLength} characters after trimming and must not be blank.")
            // SEC-1: a NUL would reach the `varchar(500)` column and be refused by PostgreSQL (22021), which
            // is an unhandled 500 rather than this coded 400. No other character is refused.
            .Must(reason => reason is null || !reason.Contains('\0'))
            .WithMessage("Reason must not contain a NUL character.");
        RuleFor(command => command.Target).IsInEnum();
    }
}
