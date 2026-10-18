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
/// without telling the client which field to fix.
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
            .WithMessage($"Reason must be 1 to {MaxReasonLength} characters after trimming and must not be blank.");
        RuleFor(command => command.Target).IsInEnum();
    }
}
