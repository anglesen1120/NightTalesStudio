using FluentValidation;

namespace NightTales.Application.Features.Stories.Commands.EditStory;

/// <summary>
/// Shape validation of <c>PUT /episodes/{id}/story</c>. The <c>If-Match</c> header is not checked here: its absence is a 428
/// <c>if_match_required</c> and a stale or malformed one a 412, both answered by <see cref="StoryEtag"/> in the handler
/// (a 400 would hide them). The D3 content rules are the domain validator's, run by the handler.
/// </summary>
public sealed class EditStoryCommandValidator : AbstractValidator<EditStoryCommand>
{
    /// <summary>Creates the validator.</summary>
    public EditStoryCommandValidator()
    {
        RuleFor(command => command.EpisodeId).NotEmpty();
        RuleFor(command => command.Content).NotNull();
    }
}
