using ErrorOr;
using Mediator;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;

namespace NightTales.Application.Features.Publishing.Queries.GetConsentText;

/// <summary>
/// Serves the <c>pc.v1</c> publication-consent text in one locale (06 README D7; 10 CR-17): the markdown the
/// consent dialogue of 10.7.2-T1 renders, plus the <c>YouTube:ConsentTextVersion</c> the client must echo back to
/// <c>POST /api/v1/publications/{id}/consent</c> (the command compares exactly that string, 06.2.2-T1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordinal locale match before any resource name is built.</b> Only the two wire values <c>vi</c> and <c>en</c>
/// select a resource; <see cref="GetConsentTextValidator"/> refuses every other value in the pipeline, and this
/// handler repeats that refusal — the same <c>validation_failed</c> code, field <c>locale</c> and message — for a
/// direct caller, so a query value can never steer the manifest lookup or answer text the build did not ship. The
/// comparison is ordinal: <c>VI</c> is not <c>vi</c>, because the client sends the lower-case wire form 10.7.2-T1
/// declares.
/// </para>
/// <para>
/// <b>One manifest read per locale, once per process.</b> Each text is held in a <see cref="Lazy{T}"/> and read
/// from the assembly's embedded resource on first use, so no request does manifest I/O after the first, and a
/// resource the build did not embed fails loudly instead of answering an empty consent text.
/// </para>
/// <para>
/// <b>Version from the live option.</b> <see cref="YouTubeOptions"/> arrives as the DI-registered scoped snapshot of
/// <c>IOptionsMonitor&lt;YouTubeOptions&gt;.CurrentValue</c> (the composition root resolves it, as every 06 handler
/// reads the section; the Application assembly itself does not reference <c>Microsoft.Extensions.Options</c>), so
/// <see cref="ConsentTextDto.Version"/> always names the version the consent command will accept, never a
/// compiled-in constant.
/// </para>
/// <para>
/// <b>The version is an option; the text is a file — so they ship together.</b> The version this handler answers is
/// only the configured value; the words it answers are the embedded <c>pc.v1.&lt;locale&gt;.md</c> template, whose
/// resource name carries no version of its own. Bumping <c>YouTube:ConsentTextVersion</c> therefore
/// <em>requires</em> shipping the matching new template files in the same release: a bump without new texts would
/// label the v1 text with the new version, and every consent recorded afterwards would name a version whose words
/// the owner never saw. Tracked as an open item for the owner and the 13 compliance review.
/// </para>
/// </remarks>
/// <param name="options">The bound <c>YouTube</c> section; only <see cref="YouTubeOptions.ConsentTextVersion"/> is read.</param>
public sealed class GetConsentTextHandler(YouTubeOptions options)
    : IQueryHandler<GetConsentTextQuery, ErrorOr<ConsentTextDto>>
{
    /// <summary>The request field every locale refusal answers on (<c>errors.locale</c>).</summary>
    private const string LocaleField = "locale";

    /// <summary>The Vietnamese wire locale.</summary>
    private const string VietnameseLocale = "vi";

    /// <summary>The English wire locale.</summary>
    private const string EnglishLocale = "en";

    /// <summary>The logical-name prefix both embedded templates share (<c>pc.v1.&lt;locale&gt;.md</c> follows it).</summary>
    private const string LogicalNamePrefix = "NightTales.Application.Templates.Consent.pc.v1.";

    /// <summary>The Vietnamese text; read on first use and kept for the process.</summary>
    private static readonly Lazy<string> VietnameseText = new(() => Read(VietnameseLocale));

    /// <summary>The English text; read on first use and kept for the process.</summary>
    private static readonly Lazy<string> EnglishText = new(() => Read(EnglishLocale));

    /// <inheritdoc />
    public ValueTask<ErrorOr<ConsentTextDto>> Handle(GetConsentTextQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var text = Text(query.Locale);
        if (text is null)
        {
            // The validator refuses these before the handler runs; the guard repeats the same answer for a direct
            // caller and, more importantly, keeps the resource-name lookup unreachable from unvalidated input.
            return ValueTask.FromResult<ErrorOr<ConsentTextDto>>(
                Errors.ValidationFailed(LocaleField, GetConsentTextValidator.LocaleUnknownMessage));
        }

        return ValueTask.FromResult<ErrorOr<ConsentTextDto>>(
            new ConsentTextDto(options.ConsentTextVersion, text));
    }

    /// <summary>The embedded text of one wire locale, or <see langword="null"/> for any other value.</summary>
    /// <param name="locale">The query's locale, in its lower-case wire form.</param>
    /// <returns>The markdown body, or <see langword="null"/> when no template matches.</returns>
    private static string? Text(string? locale) => locale switch
    {
        VietnameseLocale => VietnameseText.Value,
        EnglishLocale => EnglishText.Value,
        _ => null,
    };

    /// <summary>Reads one embedded template; a resource the build did not embed is a deployment bug.</summary>
    /// <param name="locale">The validated wire locale.</param>
    /// <returns>The template's text, as shipped.</returns>
    private static string Read(string locale)
    {
        var logicalName = LogicalNamePrefix + locale + ".md";
        var assembly = typeof(GetConsentTextHandler).Assembly;

        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"The assembly '{assembly.GetName().Name}' has no embedded resource named '{logicalName}'.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
