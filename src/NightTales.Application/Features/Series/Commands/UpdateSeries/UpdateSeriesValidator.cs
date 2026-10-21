using FluentValidation;
using NightTales.Application.Features.Series.Commands.CreateSeries;

namespace NightTales.Application.Features.Series.Commands.UpdateSeries;

/// <summary>
/// Shape validation of <c>PATCH /api/v1/series/{id}</c> (09.4.1 plan §4 U2): every rule applies only to
/// a member the patch <em>carries</em>, because an absent member is a valid "leave it as it is".
/// </summary>
/// <remarks>
/// <para>
/// The patch itself is never null: the Api's wire model builds it from the parsed body, so the endpoint
/// always has a <c>SeriesPatch</c> to hand over (09.4.1 plan §5).
/// </para>
/// <para>
/// The bounds are the create route's, imported rather than repeated, so the two routes cannot drift
/// (00 §6, 09 README D16). Whether the named voice, template and preset exist is the handler's check,
/// exactly as on create.
/// </para>
/// </remarks>
public sealed class UpdateSeriesValidator : AbstractValidator<UpdateSeriesCommand>
{
    public UpdateSeriesValidator()
    {
        // Every rule is declared on `command.Patch.<Member>`, which would report the failure under the
        // nested path `patch.title`; OverridePropertyName pins the top-level wire name the client sent
        // (09 README D14), which is also the name the handler's own 400s use (CR-1 on 09.4.1-T1).
        When(command => command.Patch.Title is not null, () =>
        {
            RuleFor(command => command.Patch.Title!)
                .Must(title => title!.Trim().Length >= CreateSeriesValidator.TitleMinLength)
                .WithMessage("title_required")
                .Must(title => title!.Trim().Length <= CreateSeriesValidator.TitleMaxLength)
                .WithMessage("title_too_long")
                .OverridePropertyName("title");
        });

        When(command => command.Patch.VoiceId is not null, () =>
        {
            RuleFor(command => command.Patch.VoiceId!)
                .NotEmpty().WithMessage("voice_id_required")
                .MaximumLength(CreateSeriesValidator.VoiceIdMaxLength).WithMessage("voice_id_too_long")
                .OverridePropertyName("voiceId");
        });

        When(command => command.Patch.StyleTemplateId is not null, () =>
        {
            RuleFor(command => command.Patch.StyleTemplateId)
                .NotEmpty().WithMessage("style_template_id_required")
                .OverridePropertyName("styleTemplateId");
        });

        // The enum converter accepts integers (09 README D14), so an undefined member must be refused
        // before the handler reads it as a topic or an aspect the database would reject.
        When(command => command.Patch.Topic is not null, () =>
        {
            RuleFor(command => command.Patch.Topic!.Value)
                .IsInEnum().WithMessage("topic_unknown")
                .OverridePropertyName("topic");
        });

        When(command => command.Patch.Aspect is not null, () =>
        {
            RuleFor(command => command.Patch.Aspect!.Value)
                .IsInEnum().WithMessage("aspect_unknown")
                .OverridePropertyName("aspect");
        });
    }
}
