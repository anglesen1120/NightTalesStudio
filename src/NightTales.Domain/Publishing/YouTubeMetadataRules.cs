using System.Text;
using ErrorOr;

namespace NightTales.Domain.Publishing;

/// <summary>
/// The YouTube metadata contract of 06 README D6, as G4 and G6 state the platform's own limits: the title is
/// 1–100 characters, the description 1–5,000 <em>UTF-8 bytes</em> (F08) counted after the render's music
/// attribution was appended (C8), the tags are at most 15 with a G4-counted length of 500 (F09), the privacy is
/// one of <c>public</c>/<c>unlisted</c>/<c>private</c>, and a <c>publishAt</c> is either absent or at least
/// <c>YouTubeOptions.PublishLeadMinutes</c> ahead. <c>videos.insert</c> is the last line of defence: a value that
/// passes here must not be refused by Google for a reason this class could have named.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every violation, not the first one.</b> <see cref="Validate"/> answers with one
/// <see cref="ErrorType.Validation"/> error per broken rule, each carrying the request field in
/// <c>metadata["field"]</c> — <c>title</c>, <c>description</c>, <c>tags</c>, <c>privacy</c> or <c>publishAt</c> —
/// so 09.1.4-T1's writer answers 400 <c>validation_failed</c> with a complete <c>errors</c> map instead of
/// making the client fix one field per round trip. The one exception is an over-long tag list, which answers with
/// the count violation alone: it is bounded only by the request body, so walking it would cost one error per
/// element (see <see cref="ValidateTags"/>).
/// </para>
/// <para>
/// <b>Characters versus bytes.</b> YouTube counts characters, and a surrogate pair is one character: the title
/// and every tag are measured with <see cref="RuneCount"/>, never with <c>string.Length</c>, so 100 astral
/// characters are accepted while 101 are refused. The description is the exception and is counted in UTF-8 bytes
/// (F08), because that is the limit the API actually applies.
/// </para>
/// <para>
/// <b>What this class does not do.</b> It never rewrites the text: the caller trims and normalizes before it
/// calls (the <c>CreateEpisodeHandler</c> precedent), so what was validated is what is stored. It also does not
/// re-implement 02 D3's 1,000-character cap on <em>generated</em> descriptions, nor the attribution rules of the
/// music catalog — both are their owners' rules.
/// </para>
/// </remarks>
public static class YouTubeMetadataRules
{
    /// <summary>The longest accepted title, in characters (G4).</summary>
    public const int TitleMaxChars = 100;

    /// <summary>The longest accepted description, in UTF-8 bytes, attribution included (G4, F08).</summary>
    public const int DescriptionMaxBytes = 5000;

    /// <summary>The longest accepted tag list as YouTube counts it: commas and implicit quotes included (G4, F09).</summary>
    public const int TagsMaxLength = 500;

    /// <summary>The most tags one video may carry (02 D3, G4).</summary>
    public const int TagsMaxCount = 15;

    /// <summary>The longest accepted single tag, in characters.</summary>
    public const int TagMaxChars = 30;

    /// <summary>
    /// The catalogued code of a 400 that names its fields (09 §5.4): every violation answers with it, and the
    /// catalogue constant is <c>NightTales.Application.Common.Errors.ErrorCodes.ValidationFailed</c>, which the
    /// Domain may not reference — the unit test keeps the two equal.
    /// </summary>
    public const string ValidationFailedCode = "validation_failed";

    /// <summary>
    /// The metadata key under which every violation names the request field that broke the rule. It is
    /// <c>NightTales.Application.Common.Errors.ErrorMetadata.Field</c>, which the Domain may not reference; the
    /// ProblemDetails writer groups the <c>errors</c> object by it.
    /// </summary>
    public const string FieldMetadataKey = "field";

    /// <summary>The request field a title violation answers on.</summary>
    private const string TitleField = "title";

    /// <summary>The request field a description violation answers on.</summary>
    private const string DescriptionField = "description";

    /// <summary>The request field a tag violation answers on.</summary>
    private const string TagsField = "tags";

    /// <summary>The request field a privacy violation answers on.</summary>
    private const string PrivacyField = "privacy";

    /// <summary>The request field a publish-time violation answers on.</summary>
    private const string PublishAtField = "publishAt";

    /// <summary>The tail the render's music attribution is appended with (C8, ruling T9): blank line, then <c>Music: </c>.</summary>
    private const string AttributionPrefix = "\n\nMusic: ";

    /// <summary>The visibilities YouTube's <c>privacyStatus</c> accepts (G6), as the <c>privacy</c> column stores them.</summary>
    private static readonly string[] AllowedPrivacy = ["public", "unlisted", "private"];

    /// <summary>
    /// The description as it will be uploaded: <paramref name="description"/> with the render's music attribution
    /// appended as <c>"\n\nMusic: " + attribution</c> (C8, ruling T9).
    /// </summary>
    /// <remarks>
    /// The create handler calls this <em>before</em> <see cref="Validate"/>, because the 5,000-byte cap counts the
    /// bytes of the description YouTube receives, not the bytes the owner typed (F08).
    /// </remarks>
    /// <param name="description">The submitted description.</param>
    /// <param name="attribution">
    /// The attribution of the render's music track (<c>render-params.v1.music.attribution</c>), or
    /// <see langword="null"/>/empty when the render used no track and when the episode has no render job at all
    /// (05 README:332-336). A blank value is treated as absent, so no description ever ends in a <c>Music: </c>
    /// tail with nothing after it.
    /// </param>
    /// <returns>The description to store and to send to YouTube.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    public static string AppendAttribution(string description, string? attribution)
    {
        ArgumentNullException.ThrowIfNull(description);

        return string.IsNullOrWhiteSpace(attribution)
            ? description
            : description + AttributionPrefix + attribution;
    }

    /// <summary>
    /// The length YouTube assigns to a tag list (G4): the sum of the tags, two extra characters for every tag that
    /// contains a space (the quotes YouTube adds around it in the <c>tags</c> field), plus the comma between every
    /// two consecutive tags. An empty list is 0, not the −1 the formula would give.
    /// </summary>
    /// <param name="tags">The tags, in submitted order; a null or empty element counts as empty.</param>
    /// <returns>The G4-counted length, compared against <see cref="TagsMaxLength"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tags"/> is <see langword="null"/>.</exception>
    public static int TagsLength(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        if (tags.Count == 0)
        {
            return 0;
        }

        var length = tags.Count - 1;
        foreach (var tag in tags)
        {
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            length += RuneCount(tag) + (tag.Contains(' ') ? 2 : 0);
        }

        return length;
    }

    /// <summary>
    /// Every D6 rule of one submission, as the values will be uploaded: the title, the description (already
    /// carrying the attribution — see <see cref="AppendAttribution"/>), the tags, the privacy and the publish
    /// instant.
    /// </summary>
    /// <remarks>
    /// The handler validates the <em>submitted</em> privacy, before it applies the pre-audit override of 06 README
    /// D12, so a client that asks for <c>public</c> on a video the channel's audit status forces to <c>private</c>
    /// is told nothing about a rule it did not break.
    /// </remarks>
    /// <param name="title">The submitted title; 1–100 characters, no <c>&lt;</c>/<c>&gt;</c>, no control characters.</param>
    /// <param name="description">The submitted description with the attribution appended; 1–5,000 UTF-8 bytes.</param>
    /// <param name="tags">The submitted tags; at most 15, each 1–30 characters, G4-length at most 500.</param>
    /// <param name="privacy">The submitted visibility; <c>public</c>, <c>unlisted</c> or <c>private</c> (G6).</param>
    /// <param name="publishAt">The submitted instant, or <see langword="null"/>; at least the lead time ahead (D6).</param>
    /// <param name="now">The command's clock reading; the domain never reads the clock itself.</param>
    /// <param name="publishLeadMinutes"><c>YouTubeOptions.PublishLeadMinutes</c> (D11), 120 by default.</param>
    /// <returns>
    /// <c>Success</c>, or every violation as one <see cref="ErrorType.Validation"/> error carrying its
    /// <c>field</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="title"/>, <paramref name="description"/>, <paramref name="tags"/> or
    /// <paramref name="privacy"/> is <see langword="null"/> — the shape validator of the route has already refused
    /// a body that could produce that, so it is a bug, not a 400.
    /// </exception>
    public static ErrorOr<Success> Validate(
        string title,
        string description,
        IReadOnlyList<string> tags,
        string privacy,
        DateTimeOffset? publishAt,
        DateTimeOffset now,
        int publishLeadMinutes)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(privacy);

        var errors = new List<Error>();

        ValidateTitle(title, errors);
        ValidateDescription(description, errors);
        ValidateTags(tags, errors);

        if (!AllowedPrivacy.Contains(privacy, StringComparer.Ordinal))
        {
            errors.Add(Violation(PrivacyField, "The privacy must be public, unlisted or private."));
        }

        // The boundary is inclusive (06.2.1-T1 failure table): exactly now + the lead time is accepted, because
        // the queue time it maps to is already the earliest instant the scheduler can honour (D12).
        if (publishAt is { } scheduledAt && scheduledAt < now.AddMinutes(publishLeadMinutes))
        {
            errors.Add(Violation(
                PublishAtField,
                $"The publish time must be at least {publishLeadMinutes} minutes ahead."));
        }

        return errors.Count == 0 ? Result.Success : errors;
    }

    /// <summary>Whether <paramref name="value"/> holds a control character the metadata contract refuses.</summary>
    /// <param name="value">The title, description or tag under test.</param>
    /// <param name="allowLineBreaks">
    /// Whether <c>\n</c>, <c>\r</c> and <c>\t</c> are allowed (the description, whose text is free-form); the
    /// title and a tag carry no line break at all.
    /// </param>
    private static bool HasControlCharacter(string value, bool allowLineBreaks)
    {
        foreach (var character in value)
        {
            if (character == '\u007F')
            {
                return true;
            }

            if (character >= '\u0020')
            {
                continue;
            }

            if (allowLineBreaks && character is '\n' or '\r' or '\t')
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// The number of characters YouTube counts in <paramref name="value"/>: Unicode code points, so an astral
    /// character (an emoji, a rare CJK glyph) counts once even though it is a surrogate pair in UTF-16.
    /// </summary>
    private static int RuneCount(string value) => value.EnumerateRunes().Count();

    /// <summary>The title rules of G4/D6: 1–100 characters, no angle brackets, no control characters.</summary>
    private static void ValidateTitle(string title, List<Error> errors)
    {
        if (RuneCount(title) is < 1 or > TitleMaxChars)
        {
            errors.Add(Violation(TitleField, $"The title must be 1-{TitleMaxChars} characters."));
        }

        if (title.Contains('<') || title.Contains('>'))
        {
            errors.Add(Violation(TitleField, "The title must not contain < or >."));
        }

        if (HasControlCharacter(title, allowLineBreaks: false))
        {
            errors.Add(Violation(TitleField, "The title must not contain control characters."));
        }
    }

    /// <summary>The description rules of F08/D6: 1–5,000 UTF-8 bytes, no angle brackets, no control characters.</summary>
    private static void ValidateDescription(string description, List<Error> errors)
    {
        if (Encoding.UTF8.GetByteCount(description) is < 1 or > DescriptionMaxBytes)
        {
            errors.Add(Violation(
                DescriptionField,
                $"The description must be 1-{DescriptionMaxBytes} UTF-8 bytes, the music attribution included."));
        }

        if (description.Contains('<') || description.Contains('>'))
        {
            errors.Add(Violation(DescriptionField, "The description must not contain < or >."));
        }

        if (HasControlCharacter(description, allowLineBreaks: true))
        {
            errors.Add(Violation(DescriptionField, "The description must not contain control characters."));
        }
    }

    /// <summary>
    /// The tag rules of F09/D6: at most 15 tags, each 1–30 characters, G4-length at most 500. An over-long list is
    /// the exception to "every violation": it answers with the count violation alone and returns, because the list
    /// is bounded only by the request body (1 MiB, ~350k empty elements) and the length pass and the per-tag walk
    /// below would otherwise touch every element and answer with one <see cref="Error"/> plus its metadata
    /// dictionary per offending one.
    /// </summary>
    private static void ValidateTags(IReadOnlyList<string> tags, List<Error> errors)
    {
        if (tags.Count > TagsMaxCount)
        {
            errors.Add(Violation(TagsField, $"A video may carry at most {TagsMaxCount} tags."));
            return;
        }

        if (TagsLength(tags) > TagsMaxLength)
        {
            errors.Add(Violation(
                TagsField,
                $"The tags must be at most {TagsMaxLength} characters as YouTube counts them (commas and the quotes around a tag with a space included)."));
        }

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                errors.Add(Violation(TagsField, "A tag must not be blank."));
                continue;
            }

            if (RuneCount(tag) > TagMaxChars)
            {
                errors.Add(Violation(TagsField, $"A tag must be at most {TagMaxChars} characters."));
                continue;
            }

            if (tag.Contains('<') || tag.Contains('>') || HasControlCharacter(tag, allowLineBreaks: false))
            {
                errors.Add(Violation(TagsField, "A tag must not contain <, > or control characters."));
            }
        }
    }

    /// <summary>One violated rule, on the request field that broke it.</summary>
    private static Error Violation(string field, string description) => Error.Validation(
        ValidationFailedCode,
        description,
        new Dictionary<string, object>(StringComparer.Ordinal) { [FieldMetadataKey] = field });
}
