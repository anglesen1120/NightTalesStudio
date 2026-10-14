using FluentValidation;

namespace NightTales.Application.Features.Research.Commands.UpdateFactSheet;

/// <summary>
/// Shape validation of <c>PUT /api/v1/episodes/{id}/fact-sheet</c>. The <c>If-Match</c> header is not checked
/// here: its absence is a 428 <c>if_match_required</c> and a stale or foreign tag a 412
/// <c>fact_sheet_conflict</c>, both answered by the handler through <c>ETags</c> (a 400 would hide them). The
/// fact-body rules are the handler's, because they need the episode's current version and its sources.
/// </summary>
public sealed class UpdateFactSheetCommandValidator : AbstractValidator<UpdateFactSheetCommand>
{
    /// <summary>Creates the validator.</summary>
    public UpdateFactSheetCommandValidator()
    {
        RuleFor(command => command.EpisodeId).NotEmpty();
        RuleFor(command => command.Content).NotNull();
    }
}
