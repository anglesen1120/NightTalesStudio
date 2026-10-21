using FluentValidation;

namespace NightTales.Application.Features.Stories.Commands.ReopenStory;

/// <summary>Validates route shape; state and If-Match preconditions belong to the ordered handler checks.</summary>
public sealed class ReopenStoryCommandValidator : AbstractValidator<ReopenStoryCommand>
{
    /// <summary>Creates the validator.</summary>
    public ReopenStoryCommandValidator()
    {
        RuleFor(command => command.EpisodeId).NotEmpty();
    }
}
