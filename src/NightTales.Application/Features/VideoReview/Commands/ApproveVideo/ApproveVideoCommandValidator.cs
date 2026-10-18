using FluentValidation;

namespace NightTales.Application.Features.VideoReview.Commands.ApproveVideo;

/// <summary>
/// Validates the acceptance's shape (the note's length and characters), run by the D4
/// <c>ValidationBehavior</c> before the handler; the state and <c>If-Match</c> preconditions belong to the
/// handler's ordered checks.
/// </summary>
/// <remarks>
/// The note is not stored, but a NUL (U+0000) in it is refused all the same: it is the character PostgreSQL
/// cannot carry, so accepting it here would push the refusal to a later writer and turn the coded 400 into a
/// raw 500. Every other character the owner typed is accepted.
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
