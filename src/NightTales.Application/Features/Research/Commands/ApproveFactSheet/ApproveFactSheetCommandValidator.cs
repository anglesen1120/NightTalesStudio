using FluentValidation;

namespace NightTales.Application.Features.Research.Commands.ApproveFactSheet;

/// <summary>
/// Shape validation of <c>POST /api/v1/episodes/{id}/fact-sheet/approve</c>. The <c>If-Match</c> header, the
/// episode's state and the F13 gate stay ordered in the handler, because each answers its own status (428, 412,
/// 409) that a 400 would hide.
/// </summary>
public sealed class ApproveFactSheetCommandValidator : AbstractValidator<ApproveFactSheetCommand>
{
    /// <summary>Creates the validator.</summary>
    public ApproveFactSheetCommandValidator()
    {
        RuleFor(command => command.EpisodeId).NotEmpty();
    }
}
