using FluentValidation;

namespace NightTales.Application.Features.Publishing.Queries.GetConsentText;

/// <summary>
/// Shape validation of <c>GET /api/v1/publications/consent-text</c> (06.2.2-T1, 06 README D7; 10 CR-17), run by the
/// <c>ValidationBehavior</c> before the handler: <c>?locale=</c> must be exactly <c>vi</c> or <c>en</c>, reported under
/// the wire field <c>locale</c> as <see cref="LocaleUnknownMessage"/>.
/// </summary>
/// <remarks>
/// <para>
/// The match is ordinal and lower-case — the wire values 10.7.2-T1 sends — so <c>VI</c> is a failure, not a silent
/// fallback. An empty value is a failure too: the client sent it, and treating it as "the default locale" would hide a
/// picker bug the consent dialogue must never paper over.
/// </para>
/// <para>
/// <see cref="GetConsentTextHandler"/> repeats the same refusal with the same message for a caller that invokes the
/// handler directly, so no locale can reach the resource-name lookup on either path.
/// </para>
/// </remarks>
public sealed class GetConsentTextValidator : AbstractValidator<GetConsentTextQuery>
{
    /// <summary>
    /// The per-field wire string every locale refusal carries (validator codes, not copy: 09 README D9). The handler
    /// reuses it, so both paths answer one shape.
    /// </summary>
    internal const string LocaleUnknownMessage = "locale_unknown";

    public GetConsentTextValidator()
    {
        RuleFor(query => query.Locale)
            .Must(BeAWireLocale)
            .WithMessage(LocaleUnknownMessage);
    }

    /// <summary>Whether the value is one of the two lower-case locales the templates exist for.</summary>
    /// <param name="locale">The query's locale, possibly null or empty.</param>
    /// <returns><see langword="true"/> for exactly <c>vi</c> or <c>en</c>.</returns>
    private static bool BeAWireLocale(string? locale) => locale is "vi" or "en";
}
