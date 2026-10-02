using FluentValidation;

namespace NightTales.Application.Features.Me.Commands.UpdateMe;

/// <summary>Shape validation of <c>PATCH /me</c> (09.3.4-T1): something to change, and a clean display name.</summary>
public sealed class UpdateMeValidator : AbstractValidator<UpdateMeCommand>
{
    /// <summary>The display name bounds (<c>display_name varchar(80)</c>).</summary>
    public const int DisplayNameMaxLength = 80;

    public UpdateMeValidator()
    {
        RuleFor(command => command)
            .Must(command => command.DisplayName is not null || command.UiLocale is not null)
            .WithErrorCode("required")
            .WithMessage("required")
            .OverridePropertyName("displayName");

        When(command => command.DisplayName is not null, () =>
        {
            RuleFor(command => command.DisplayName!)
                .Length(1, DisplayNameMaxLength).WithMessage("display_name_length")
                .Must(value => value == value.Trim()).WithMessage("display_name_untrimmed")
                .Must(value => !value.Any(char.IsControl)).WithMessage("display_name_control_characters")
                .OverridePropertyName("displayName");
        });

        When(command => command.UiLocale is not null, () =>
        {
            RuleFor(command => command.UiLocale!.Value)
                .IsInEnum().WithMessage("ui_locale_unknown")
                .OverridePropertyName("uiLocale");
        });
    }
}
