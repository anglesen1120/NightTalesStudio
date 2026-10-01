using FluentValidation;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Episodes.Commands.CreateEpisode;

/// <summary>
/// Shape validation of <c>POST /api/v1/series/{seriesId}/episodes</c> (09.5.1 plan §4 UC1), run by the
/// D4 <c>ValidationBehavior</c> before the handler.
/// </summary>
/// <remarks>
/// <para>
/// Only what the wire can decide is here: the brief's 1–1,000 characters <em>after trimming</em> and its
/// control characters, the title's 120-character bound after trimming, and a defined
/// <see cref="LengthTier"/>. Whether the series exists, belongs to the caller and is not archived needs
/// another row, so those are the handler's 404/409 (09.5.1 plan §4 UC1).
/// </para>
/// <para>
/// The three brief rules cascade, so a too-long brief reports one failure under field <c>brief</c>
/// instead of three; the failure is always grouped under the wire name (09 README D7), which is why no
/// rule needs <c>OverridePropertyName</c> here — the members are top-level on this command.
/// </para>
/// </remarks>
public sealed class CreateEpisodeValidator : AbstractValidator<CreateEpisodeCommand>
{
    /// <summary>The shortest accepted brief, after trimming (00 §6 <c>episodes.brief</c>).</summary>
    public const int BriefMinLength = 1;

    /// <summary>The longest accepted brief, after trimming; also the <c>episodes.brief</c> column.</summary>
    public const int BriefMaxLength = 1_000;

    /// <summary>The longest accepted title, after trimming; also the <c>episodes.title</c> column.</summary>
    public const int TitleMaxLength = 120;

    public CreateEpisodeValidator()
    {
        RuleFor(command => command.Brief)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("brief_required")
            .Must(brief => brief.Trim().Length >= BriefMinLength).WithMessage("brief_required")
            // The bound is on the stored value, which the handler trims, so a padded 1,000-character
            // brief is accepted and a padded 1,001-character one is not.
            .Must(brief => brief.Trim().Length <= BriefMaxLength).WithMessage("brief_too_long")
            .Must(OnlyAllowedControlCharacters).WithMessage("brief_control_characters");

        // An empty title is not an error: it is normalized to `null` by the handler (09.5.1 plan §Design).
        RuleFor(command => command.Title)
            .Must(title => title is null || title.Trim().Length <= TitleMaxLength)
            .WithMessage("title_too_long");

        // The enum converter accepts integer values (09 README D14), so an undefined member would
        // otherwise reach the table's CHECK constraint as a 500.
        RuleFor(command => command.LengthTier).IsInEnum().WithMessage("length_tier_unknown");
    }

    /// <summary>
    /// The brief is plain text: a line feed and a tab are allowed, every other control character (a
    /// carriage return, a NUL, a vertical tab) is not.
    /// </summary>
    private static bool OnlyAllowedControlCharacters(string brief) =>
        !brief.Any(character => char.IsControl(character) && character is not ('\n' or '\t'));
}
