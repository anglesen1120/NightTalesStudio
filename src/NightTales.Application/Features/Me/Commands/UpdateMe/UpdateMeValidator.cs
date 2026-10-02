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
                .Must(value => !ContainsInvisibleFormatting(value)).WithMessage("display_name_invisible_characters")
                .OverridePropertyName("displayName");
        });

        When(command => command.UiLocale is not null, () =>
        {
            RuleFor(command => command.UiLocale!.Value)
                .IsInEnum().WithMessage("ui_locale_unknown")
                .OverridePropertyName("uiLocale");
        });
    }

    /// <summary>
    /// Whether the name carries a Unicode format (Cf: zero-width, bidi override/isolate), line-separator (Zl) or
    /// paragraph-separator (Zp) character. Such characters render as nothing or reorder the text around them, so a
    /// name made of them (or hiding them) would impersonate another account. Any name that is empty once its format
    /// characters are removed contains one, so it is refused here too. Runes, not chars, so supplementary-plane
    /// format characters (tag characters) are caught.
    /// </summary>
    private static bool ContainsInvisibleFormatting(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }
        }

        return false;
    }
}
