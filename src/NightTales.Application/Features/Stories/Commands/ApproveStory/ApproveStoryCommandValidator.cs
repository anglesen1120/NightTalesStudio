using FluentValidation;

namespace NightTales.Application.Features.Stories.Commands.ApproveStory;

/// <summary>Validates command shape; state and ETag checks stay ordered in the handler, attestation in the gate.</summary>
public sealed class ApproveStoryCommandValidator : AbstractValidator<ApproveStoryCommand>
{
    /// <summary>Creates the validator.</summary>
    public ApproveStoryCommandValidator()
    {
        RuleFor(command => command.EpisodeId).NotEmpty();
        RuleFor(command => command.Source).IsInEnum();
    }
}
