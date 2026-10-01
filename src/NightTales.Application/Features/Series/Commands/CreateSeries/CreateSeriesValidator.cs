using FluentValidation;

namespace NightTales.Application.Features.Series.Commands.CreateSeries;

/// <summary>
/// Shape validation of <c>POST /api/v1/series</c> (09.4.1 plan §4 U1), run by the D4
/// <c>ValidationBehavior</c> before the handler.
/// </summary>
/// <remarks>
/// Only what the wire can decide is here: the title's 1–120 characters <em>after trimming</em>, the
/// voice id's length, a non-empty style-template id and defined enum members. Whether the voice, the
/// template and the preset exist — and whether they fit the topic and the language — needs other rows
/// or a catalog, so those are the handler's 400s, each carrying its own field
/// (<c>voiceId</c>, <c>styleTemplateId</c>, <c>storyPreset</c>).
/// </remarks>
public sealed class CreateSeriesValidator : AbstractValidator<CreateSeriesCommand>
{
    /// <summary>The shortest accepted title, after trimming (00 §6, 09 README D16).</summary>
    public const int TitleMinLength = 1;

    /// <summary>The longest accepted title, after trimming; also the <c>series.title</c> column.</summary>
    public const int TitleMaxLength = 120;

    /// <summary>The longest accepted voice id, matching the 04 D9 canonical name (1–64 code points).</summary>
    public const int VoiceIdMaxLength = 64;

    public CreateSeriesValidator()
    {
        RuleFor(command => command.Title)
            .NotEmpty().WithMessage("title_required")
            // The bound is on the stored value, which Create trims, so a padded 120-character title is
            // accepted and a padded 121-character one is not.
            .Must(title => title is not null && title.Trim().Length <= TitleMaxLength)
            .WithMessage("title_too_long");

        RuleFor(command => command.VoiceId)
            .NotEmpty().WithMessage("voice_id_required")
            .MaximumLength(VoiceIdMaxLength).WithMessage("voice_id_too_long");

        RuleFor(command => command.StyleTemplateId)
            .NotEmpty().WithMessage("style_template_id_required");

        // The enum converter accepts integer values (09 README D14), so an undefined member would
        // otherwise reach the handler as a topic, a language or an aspect the database rejects.
        RuleFor(command => command.Topic).IsInEnum().WithMessage("topic_unknown");
        RuleFor(command => command.Language).IsInEnum().WithMessage("language_unknown");
        RuleFor(command => command.Aspect).IsInEnum().WithMessage("aspect_unknown");
    }
}
