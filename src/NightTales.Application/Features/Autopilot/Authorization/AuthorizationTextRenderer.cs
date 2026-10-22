using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Authorization;

/// <summary>
/// The inputs of one rendered authorization text (02.8.3-T2, plan §3.7): the wire locale the owner reads, the
/// series and channel the upload is authorized for, the stored settings that decide the cadence and the privacy,
/// and the channel's current YouTube audit status, which is the one bit the text carries that the settings do not.
/// </summary>
/// <param name="Locale">The wire locale, <c>vi</c> or <c>en</c>; any other value is refused, never defaulted.</param>
/// <param name="SeriesTitle">The series title, user text: it is inserted verbatim.</param>
/// <param name="Topic">The series topic, rendered through the label map of the task's 'Authorization text' section.</param>
/// <param name="ChannelTitle">The connected channel's title, as the platform reports it.</param>
/// <param name="Settings">The stored autopilot settings the text describes; validated before a render (task step 2).</param>
/// <param name="PreAuditPending">
/// Whether the channel's <c>youtube_audit_status</c> is still <c>pending</c>, which adds the pre-audit note. The
/// flip pending → approved changes the text, so the covered-field diff revokes and the owner re-accepts (task
/// §Failure handling).
/// </param>
public sealed record AuthorizationTextRequest(
    string Locale,
    string SeriesTitle,
    Topic Topic,
    string ChannelTitle,
    AutopilotSettings Settings,
    bool PreAuditPending);

/// <summary>
/// One rendered authorization text and the digest the owner accepts (02.8.3-T2, plan §3.7): the version the client
/// echoes back on <c>POST …/autopilot/authorization</c>, the locale it was rendered in, the exact sentence, and
/// <c>sha256(UTF-8(NFC(text)))</c> in lower-case hex.
/// </summary>
/// <param name="TextVersion">The template version, always <see cref="AuthorizationTextRenderer.CurrentTextVersion"/> today.</param>
/// <param name="Locale">The wire locale the text was rendered in (<c>vi</c> or <c>en</c>).</param>
/// <param name="Text">The rendered sentence, NFC-normalised, with every placeholder substituted.</param>
/// <param name="TextHash">The digest of <paramref name="Text"/>; the wire form the accept command compares.</param>
public sealed record RenderedAuthorizationText(
    string TextVersion,
    string Locale,
    string Text,
    string TextHash);

/// <summary>
/// The single place that renders the autopilot authorization text (02.8.3-T2, task 'Authorization text'): <c>GET
/// /series/{id}/autopilot</c> shows what it produced, <c>POST …/authorization</c> re-renders from the stored settings
/// and compares the digest, and 06.2.2-T3 records the version rather than the words.
/// </summary>
/// <remarks>
/// <para>
/// <b>Templates.</b> The two sentences live at <c>prompts/shared/autopilot-authorization.&lt;locale&gt;.v1.md</c>,
/// the path the task names, and are embedded into <em>this</em> assembly by
/// <c>NightTales.Application.csproj</c> (<c>EmbeddedResource Include="..\..\prompts\shared\…"</c>) — one file on
/// disk, two resource names, no second copy to drift. They are deliberately <b>not</b> entries of
/// <c>prompts/manifest.json</c>: that catalog's rule 6 refuses any <c>{{name}}</c> outside
/// <see cref="NightTales.Application.Abstractions.Prompts.PromptPlaceholders"/>, the eight story-prompt tokens,
/// while this text's ten tokens are a different closed set owned here.
/// <c>NightTales.Infrastructure.csproj</c> therefore removes these
/// two files from its own prompt-tree embed, so the catalog's rule 4b never sees an untracked runtime file; the
/// story catalog's start-up hash validation is not applied to them, and the guard against a silent wording change
/// is <see cref="CurrentTextVersion"/> — a new sentence ships as a new version, the same rule
/// <c>GetConsentTextHandler</c> documents for <c>pc.v1</c>.
/// </para>
/// <para>
/// <b>One substitution pass.</b> The sentence is user-facing and the series title is user text: it is inserted
/// verbatim, and because the scan advances over the <em>template</em> only, a title that contains
/// <c>{{daily_cap}}</c> reaches the reader as those characters and is never re-substituted.
/// </para>
/// <para>
/// <b>Joins.</b> The template carries the pre-audit note as <c> {{pre_audit_note}} </c>; when the note is empty a
/// single space is dropped at that join so no double space survives, while every space inside a substituted value
/// (a title with two spaces in it) is kept as the caller wrote it.
/// </para>
/// <para>
/// <b>Hash.</b> <c>sha256(UTF-8(NFC(text)))</c> in lower-case hex, computed over the text this method returns — the
/// text is normalised first, so the client that hashes what it displays and the server that re-renders agree on an
/// NFD-composed title (the same rule <c>RenderSubmitMapper</c> applies before counting a script).
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton by <c>AddAutopilotAuthorization</c>. Each template is read from the
/// assembly on first use and kept for the process, so a request performs no resource I/O after the first, and a
/// build that lost a template fails loudly instead of answering an empty authorization.
/// </para>
/// </remarks>
public sealed class AuthorizationTextRenderer
{
    /// <summary>
    /// The template version recorded beside an acceptance (<c>series_autopilot_authorizations.text_version</c>).
    /// A wording change ships a new <c>.vN</c> template and bumps this constant in the same release.
    /// </summary>
    public const string CurrentTextVersion = "autopilot-auth.v1";

    /// <summary>The Vietnamese wire locale.</summary>
    private const string VietnameseLocale = "vi";

    /// <summary>The English wire locale.</summary>
    private const string EnglishLocale = "en";

    /// <summary>The request field an unknown locale is answered on (<c>errors.locale</c>), as the consent query does.</summary>
    private const string LocaleField = "locale";

    /// <summary>The client-safe reason an unknown locale is refused with, the consent query's own token.</summary>
    private const string LocaleUnknown = "locale_unknown";

    /// <summary>The logical-name prefix both embedded templates share (the file name without the locale follows it).</summary>
    private const string LogicalNamePrefix =
        "NightTales.Application.Templates.Authorization.autopilot-authorization.";

    /// <summary>The extension both templates share; the version marker is part of it, so a bump needs a new file.</summary>
    private const string TemplateExtension = ".v1.md";

    /// <summary>The placeholder name group of <see cref="Placeholder"/>.</summary>
    private const string NameGroup = "name";

    /// <summary>Every wire locale, in the order the task lists them.</summary>
    private static readonly string[] SupportedLocales = [VietnameseLocale, EnglishLocale];

    /// <summary>The weekday names of the English text, indexed by <see cref="WeekdayIndex"/> (Monday first).</summary>
    private static readonly string[] EnglishDayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>The weekday names of the Vietnamese text, indexed by <see cref="WeekdayIndex"/> (Monday first).</summary>
    private static readonly string[] VietnameseDayNames =
        ["Thứ Hai", "Thứ Ba", "Thứ Tư", "Thứ Năm", "Thứ Sáu", "Thứ Bảy", "Chủ Nhật"];

    /// <summary>The <c>{{name}}</c> token form; lower-case snake_case names only, which is what the task lists.</summary>
    private static readonly Regex Placeholder =
        new(@"\{\{(?<name>[a-z_]+)\}\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The Vietnamese template; read on first use and kept for the process.</summary>
    private readonly Lazy<string> _vietnameseTemplate = new(() => Read(VietnameseLocale));

    /// <summary>The English template; read on first use and kept for the process.</summary>
    private readonly Lazy<string> _englishTemplate = new(() => Read(EnglishLocale));

    /// <summary>The wire locales a render accepts, <c>vi</c> and <c>en</c>.</summary>
    public static IReadOnlyList<string> Locales => SupportedLocales;

    /// <summary>
    /// Renders the authorization text of <paramref name="request"/> and its digest.
    /// </summary>
    /// <param name="request">The locale, the series and channel, the stored settings and the audit status.</param>
    /// <returns>
    /// The version, locale, NFC-normalised sentence and <c>sha256</c> digest, or a <c>validation_failed</c> error on
    /// field <c>locale</c> for a locale other than <c>vi</c> and <c>en</c>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The template of the requested locale is missing from the build, or carries a placeholder this renderer does not
    /// know — a deployment bug, never user input.
    /// </exception>
    public ErrorOr<RenderedAuthorizationText> Render(AuthorizationTextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SupportedLocales.Contains(request.Locale, StringComparer.Ordinal))
        {
            return Errors.ValidationFailed(LocaleField, LocaleUnknown);
        }

        var vietnamese = string.Equals(request.Locale, VietnameseLocale, StringComparison.Ordinal);
        var template = vietnamese ? _vietnameseTemplate.Value : _englishTemplate.Value;
        var text = Compose(template, Values(request, vietnamese));

        return new RenderedAuthorizationText(
            CurrentTextVersion,
            vietnamese ? VietnameseLocale : EnglishLocale,
            text,
            Hash(text));
    }

    /// <summary>
    /// The ten substitution values of one render, in the template's own token names.
    /// </summary>
    /// <param name="request">The render's inputs.</param>
    /// <param name="vietnamese">Whether the requested locale is <c>vi</c>.</param>
    /// <returns>One value per token; <c>pre_audit_note</c> is empty unless the audit is still pending.</returns>
    private static Dictionary<string, string> Values(AuthorizationTextRequest request, bool vietnamese)
    {
        var settings = request.Settings;
        var cadence = settings.Cadence;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["series_title"] = request.SeriesTitle,
            ["topic_label"] = TopicLabel(request.Topic, vietnamese),
            ["channel_title"] = request.ChannelTitle,
            ["days_of_week"] = DaysOfWeek(cadence.DaysOfWeek, vietnamese),
            ["publish_time"] = cadence.PublishTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["time_zone"] = cadence.TimeZoneId,
            ["daily_cap"] = settings.DailyCap.ToString(CultureInfo.InvariantCulture),
            ["privacy_label"] = PrivacyLabel(settings.Privacy, vietnamese),
            ["veto_window_hours"] = settings.VetoWindowHours.ToString(CultureInfo.InvariantCulture),
            ["pre_audit_note"] = request.PreAuditPending ? PreAuditNote(vietnamese) : string.Empty,
        };
    }

    /// <summary>
    /// Substitutes the template's placeholders in one pass and NFC-normalises the answer.
    /// </summary>
    /// <param name="template">The embedded sentence, already trimmed.</param>
    /// <param name="values">Every token's value.</param>
    /// <returns>The rendered, NFC-normalised sentence.</returns>
    /// <exception cref="InvalidOperationException">The template carries a token outside <paramref name="values"/>.</exception>
    private static string Compose(string template, Dictionary<string, string> values)
    {
        var builder = new StringBuilder(template.Length + 128);
        var position = 0;

        foreach (Match match in Placeholder.Matches(template))
        {
            AppendLiteral(builder, template.AsSpan(position, match.Index - position));

            var name = match.Groups[NameGroup].Value;
            if (!values.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException(
                    $"The authorization template carries the unknown placeholder '{{{{{name}}}}}'.");
            }

            // Verbatim: the replacement text is never scanned again, so user text cannot inject a token.
            builder.Append(value);
            position = match.Index + match.Length;
        }

        AppendLiteral(builder, template.AsSpan(position));

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Appends a literal stretch of the template, dropping one leading space when the output already ends with one —
    /// the join an empty <c>{{pre_audit_note}}</c> would otherwise leave as a double space.
    /// </summary>
    /// <param name="builder">The output so far.</param>
    /// <param name="literal">The template stretch, starting at a placeholder or at the end of the template.</param>
    private static void AppendLiteral(StringBuilder builder, ReadOnlySpan<char> literal)
    {
        if (literal.Length > 0 && literal[0] == ' ' && builder.Length > 0 && builder[^1] == ' ')
        {
            literal = literal[1..];
        }

        builder.Append(literal);
    }

    /// <summary>
    /// The localised weekday list of the cadence, for example <c>Mon, Thu</c> or <c>Thứ Hai, Thứ Năm</c>.
    /// </summary>
    /// <param name="days">The cadence's weekdays, in whatever order the stored document kept them.</param>
    /// <param name="vietnamese">Whether the requested locale is <c>vi</c>.</param>
    /// <returns>The names in Monday-to-Sunday order, deduplicated, joined with <c>, </c>.</returns>
    private static string DaysOfWeek(IReadOnlyList<DayOfWeek> days, bool vietnamese)
    {
        var names = vietnamese ? VietnameseDayNames : EnglishDayNames;

        var ordered = days
            .Distinct()
            .OrderBy(WeekdayIndex)
            .Select(day => names[WeekdayIndex(day)]);

        return string.Join(", ", ordered);
    }

    /// <summary>A weekday's position in the Monday-to-Sunday list the text reads in.</summary>
    /// <param name="day">The weekday.</param>
    /// <returns><c>0</c> for Monday through <c>6</c> for Sunday.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a declared <see cref="DayOfWeek"/>.</exception>
    private static int WeekdayIndex(DayOfWeek day)
    {
        var value = (int)day;

        if (value < (int)DayOfWeek.Sunday || value > (int)DayOfWeek.Saturday)
        {
            throw new ArgumentOutOfRangeException(nameof(day), day, "Unknown day of week.");
        }

        // DayOfWeek starts at Sunday; the text reads Monday first.
        return (value + 6) % 7;
    }

    /// <summary>The topic's label in the requested locale (task 'Authorization text').</summary>
    /// <param name="topic">The series topic.</param>
    /// <param name="vietnamese">Whether the requested locale is <c>vi</c>.</param>
    /// <returns>The label the sentence shows after <c>topic:</c> / <c>chủ đề:</c>.</returns>
    private static string TopicLabel(Topic topic, bool vietnamese) => (topic, vietnamese) switch
    {
        (Topic.Ghost, false) => "ghost stories",
        (Topic.Ghost, true) => "truyện ma",
        (Topic.Spiritual, false) => "spiritual stories",
        (Topic.Spiritual, true) => "truyện tâm linh",
        (Topic.TrueCrime, false) => "true crime (real people, always reviewed by me)",
        (Topic.TrueCrime, true) => "true crime (người thật, luôn do tôi duyệt)",
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unknown topic."),
    };

    /// <summary>The privacy's label in the requested locale (task 'Authorization text').</summary>
    /// <param name="privacy">The stored privacy.</param>
    /// <param name="vietnamese">Whether the requested locale is <c>vi</c>.</param>
    /// <returns>The label the sentence shows after <c>privacy</c> / <c>chế độ hiển thị</c>.</returns>
    private static string PrivacyLabel(PrivacyStatus privacy, bool vietnamese) => (privacy, vietnamese) switch
    {
        (PrivacyStatus.Public, false) => "public (everyone)",
        (PrivacyStatus.Public, true) => "công khai",
        (PrivacyStatus.Unlisted, false) => "unlisted (link only)",
        (PrivacyStatus.Unlisted, true) => "không công khai (chỉ có link)",
        (PrivacyStatus.Private, false) => "private",
        (PrivacyStatus.Private, true) => "riêng tư",
        _ => throw new ArgumentOutOfRangeException(nameof(privacy), privacy, "Unknown privacy."),
    };

    /// <summary>The pre-audit note, shown only while the channel's YouTube audit is still pending.</summary>
    /// <param name="vietnamese">Whether the requested locale is <c>vi</c>.</param>
    /// <returns>The note sentence, without a leading or trailing space.</returns>
    private static string PreAuditNote(bool vietnamese) => vietnamese
        ? "Cho đến khi YouTube duyệt API, các video chỉ ở chế độ **riêng tư** và không được công khai."
        : "Until the YouTube API audit is approved, uploads stay **private** and are not published publicly.";

    /// <summary><c>sha256(UTF-8(text))</c> in lower-case hex, the digest the accept command compares.</summary>
    /// <param name="text">The rendered, NFC-normalised sentence.</param>
    /// <returns>64 lower-case hex characters.</returns>
    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Reads one embedded template; a resource the build did not embed is a deployment bug.</summary>
    /// <param name="locale">The validated wire locale.</param>
    /// <returns>The template's sentence, with the file's trailing newline trimmed.</returns>
    private static string Read(string locale)
    {
        var logicalName = LogicalNamePrefix + locale + TemplateExtension;
        var assembly = typeof(AuthorizationTextRenderer).Assembly;

        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"The assembly '{assembly.GetName().Name}' has no embedded resource named '{logicalName}'.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd().Trim();
    }
}
