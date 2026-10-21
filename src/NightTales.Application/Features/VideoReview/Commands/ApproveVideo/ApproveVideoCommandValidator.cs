using FluentValidation;

namespace NightTales.Application.Features.VideoReview.Commands.ApproveVideo;

/// <summary>
/// Validates the acceptance's shape (the note's length and characters), run by the D4
/// <c>ValidationBehavior</c> before the handler; the state and <c>If-Match</c> preconditions belong to the
/// handler's ordered checks.
/// </summary>
/// <remarks>
/// The note is not stored and the audit row only records that one was given, so nothing here can fail on a NUL
/// today. A NUL (U+0000) is refused all the same, for symmetry with the reject reason, so a later writer of the
/// note never has to turn it into a raw 500 (PostgreSQL cannot carry the character). Every other character the
/// owner typed is accepted.
/// </remarks>
public sealed class ApproveVideoCommandValidator : AbstractValidator<ApproveVideoCommand>
{
    /// <summary>The longest note the route accepts.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>Creates the validator.</summary>
    public ApproveVideoCommandValidator()
    {
        RuleFor(command => command.EpisodeId).NotEmpty();
        RuleFor(command => command.Note)
            .MaximumLength(MaxNoteLength)
            // SEC-1: refuse only a NUL (the character PostgreSQL rejects), never another control character.
            .Must(note => note is null || !note.Contains('\0'))
            .WithMessage("The note must not contain a NUL character.");
    }
}
