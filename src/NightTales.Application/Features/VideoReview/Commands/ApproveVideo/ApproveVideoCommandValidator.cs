using FluentValidation;

namespace NightTales.Application.Features.VideoReview.Commands.ApproveVideo;

/// <summary>
/// Validates the acceptance's shape (the note's length), run by the D4 <c>ValidationBehavior</c> before the
/// handler; the state and <c>If-Match</c> preconditions belong to the handler's ordered checks.
/// </summary>
public sealed class ApproveVideoCommandValidator : AbstractValidator<ApproveVideoCommand>
{
    /// <summary>The longest note the route accepts.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>Creates the validator.</summary>
    public ApproveVideoCommandValidator()
    {
        RuleFor(command => command.EpisodeId).NotEmpty();
        RuleFor(command => command.Note).MaximumLength(MaxNoteLength);
    }
}
