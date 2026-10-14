using FluentValidation;

namespace NightTales.Application.Features.SeriesBible.Commands.UpdateBible;

/// <summary>
/// Shape validation of <c>PUT /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U2). The <c>If-Match</c>
/// header is not checked here: its absence is a 428 <c>if_match_required</c> and a stale or foreign tag a
/// 412 <c>bible_conflict</c>, both answered by the handler through <c>BibleEtags</c> (a 400 would hide them),
/// and only after the series is known to be the caller's. A missing <c>content</c> is likewise the handler's
/// 400 <c>bible_invalid_content</c>, not a validation failure: a rule here would run before the handler and
/// mask the 404/412 those cases must answer first. The content rules — the configured limits and the episode
/// rules — belong to the Domain validator and are run by the handler, where the series' approved episodes are
/// known.
/// </summary>
public sealed class UpdateBibleCommandValidator : AbstractValidator<UpdateBibleCommand>
{
    /// <summary>Creates the validator.</summary>
    public UpdateBibleCommandValidator()
    {
        RuleFor(command => command.SeriesId).NotEmpty();
    }
}
