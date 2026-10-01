using System.Globalization;
using System.Text;
using ErrorOr;

namespace NightTales.Domain.Catalog;

/// <summary>
/// The <c>voices.v1</c> rule set of 04 README D9 — the C# half of the one contract the tracked
/// <c>seed/voices.v1.json</c> has two readers for. The normative reader is the proxy's
/// <c>nts_vieneu_proxy.catalog.load_catalog</c>; these rules mirror its checks, its order (TL ruling
/// T14: file-level values → each entry's structure and values before the next entry → the interleaved
/// duplicate id/slug pass → the ≥ 1 active voice per language rule) and its codes, so a divergence shows
/// up as a fixture mismatch instead of as a catalog the proxy accepts and the seeder rejects.
/// </summary>
/// <remarks>
/// The rules are implemented against <em>Python</em> semantics, which D9 makes normative (T7):
/// <c>str.isalnum()</c> is a letter or a number (<c>L*</c>/<c>N*</c>, including <c>No</c> such as
/// <c>²</c>), lengths are counted in code points, <c>\d</c> is a Unicode decimal digit and a commit is
/// 40 hex characters of either case. .NET's <c>\w</c> would be wrong on the character set, so the id
/// pattern is written out with <see cref="Rune"/>.
/// </remarks>
public static class VoiceCatalogRules
{
    /// <summary>The id is not a D9 id: NFC, trimmed, 1–64 runes, no <c>-Female</c>/<c>-Male</c>/<c>:</c>, no <c>-V2</c> suffix.</summary>
    public const string VoiceIdInvalid = "voice_id_invalid";

    /// <summary>The slug is not <see cref="Slugify"/> of the id.</summary>
    public const string VoiceSlugMismatch = "voice_slug_mismatch";

    /// <summary>Any other rule of D9, at file level or on one entry's remaining fields.</summary>
    public const string CatalogInvalid = "catalog_invalid";

    /// <summary>The one schema string the file may declare.</summary>
    public const string Schema = "voices.v1";

    /// <summary>The shortest allowed id, in runes.</summary>
    public const int IdMinRunes = 1;

    /// <summary>The longest allowed id, in runes (code points, not UTF-16 units).</summary>
    public const int IdMaxRunes = 64;

    /// <summary>The longest allowed display name, in code points, after trimming.</summary>
    public const int DisplayNameMaxLength = 40;

    /// <summary>The most tags one voice may carry.</summary>
    public const int MaxTags = 4;

    /// <summary>The <see cref="Error.Metadata"/> key carrying the offending id, or a <c>null</c> value at file level.</summary>
    public const string VoiceIdMetadataKey = "voiceId";

    /// <summary>
    /// The closed tag vocabulary (D9; the 11 keys 03/10 label through <c>voices.tag.&lt;tag&gt;</c>,
    /// CR-11). Order is irrelevant — membership and count are what is checked.
    /// </summary>
    public static readonly IReadOnlySet<string> Tags = new HashSet<string>(StringComparer.Ordinal)
    {
        "storyteller",
        "natural",
        "news",
        "deep",
        "warm",
        "calm",
        "bright",
        "whisper",
        "northern",
        "central",
        "southern",
    };

    private static readonly UnicodeCategory[] LetterOrNumberCategories =
    [
        UnicodeCategory.UppercaseLetter,
        UnicodeCategory.LowercaseLetter,
        UnicodeCategory.TitlecaseLetter,
        UnicodeCategory.ModifierLetter,
        UnicodeCategory.OtherLetter,
        UnicodeCategory.DecimalDigitNumber,
        UnicodeCategory.LetterNumber,
        UnicodeCategory.OtherNumber,
    ];

    private const int SeparatorUnderscore = '_';
    private const int SeparatorSpace = ' ';
    private const int SeparatorDot = '.';
    private const int SeparatorHyphen = '-';

    private const int LatinSmallLetterDWithStroke = 0x0111;
    private const int LatinCapitalLetterDWithStroke = 0x0110;

    private const string PreviewPrefix = "voice-previews/";
    private const string PreviewVersionSeparator = ".v";
    private const string PreviewSuffix = ".mp3";

    private static readonly string[] Languages = ["vi", "en"];

    private static readonly string[] Genders = ["female", "male", "neutral"];

    /// <summary>
    /// Validates the file-level values, in D9's order (schema → revision → vieneuCommit). The key set
    /// and the JSON types are the reader's part; <paramref name="schema"/>/<paramref name="vieneuCommit"/>
    /// are <c>null</c> when they were absent or not strings, and <paramref name="revision"/> is
    /// <c>null</c> when it was absent, not a number or a boolean.
    /// </summary>
    /// <param name="schema">The declared schema, or <c>null</c>.</param>
    /// <param name="revision">The declared revision, or <c>null</c>.</param>
    /// <param name="vieneuCommit">The declared commit, or <c>null</c>.</param>
    public static ErrorOr<Success> ValidateHeader(string? schema, long? revision, string? vieneuCommit)
    {
        if (!string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            return Invalid(CatalogInvalid, $"The catalog schema must be {Schema}.", null);
        }

        if (revision is not { } value || value < 0)
        {
            return Invalid(CatalogInvalid, "The catalog revision must be a non-negative integer.", null);
        }

        if (!IsVieneuCommit(vieneuCommit))
        {
            return Invalid(CatalogInvalid, "vieneuCommit must be 40 hexadecimal characters.", null);
        }

        return Result.Success;
    }

    /// <summary>
    /// Validates one entry; the first failing rule wins, in the D9 order
    /// (id → slug → displayName → language → gender → tags → previewStorageKey).
    /// </summary>
    /// <param name="entry">The parsed entry; its fields are still raw strings.</param>
    public static ErrorOr<Success> ValidateEntry(VoiceCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!IsValidId(entry.Id))
        {
            return Invalid(VoiceIdInvalid, "The voice id must be an NFC, trimmed, 1-64 character VieNeu preset name without an alias suffix.", entry.Id);
        }

        if (!string.Equals(entry.Slug, Slugify(entry.Id), StringComparison.Ordinal))
        {
            return Invalid(VoiceSlugMismatch, "The voice slug must be the slugified voice id.", entry.Id);
        }

        var displayNameLength = PythonTrimmedRuneCount(entry.DisplayName);
        if (displayNameLength is < 1 or > DisplayNameMaxLength)
        {
            return Invalid(CatalogInvalid, "The voice display name must be 1-40 code points after trimming.", entry.Id);
        }

        if (!Languages.Contains(entry.Language, StringComparer.Ordinal))
        {
            return Invalid(CatalogInvalid, "The voice language must be vi or en.", entry.Id);
        }

        if (!Genders.Contains(entry.Gender, StringComparer.Ordinal))
        {
            return Invalid(CatalogInvalid, "The voice gender must be female, male or neutral.", entry.Id);
        }

        if (entry.Tags is null || entry.Tags.Count > MaxTags || entry.Tags.Distinct(StringComparer.Ordinal).Count() != entry.Tags.Count)
        {
            return Invalid(CatalogInvalid, "A voice carries at most four distinct tags.", entry.Id);
        }

        foreach (var tag in entry.Tags)
        {
            if (!Tags.Contains(tag))
            {
                return Invalid(CatalogInvalid, "Every voice tag must be one of the closed D9 tag list.", entry.Id);
            }
        }

        if (!IsValidPreviewStorageKey(entry.PreviewStorageKey, entry.Slug))
        {
            return Invalid(CatalogInvalid, "The preview storage key must be voice-previews/<slug>.v<n>.mp3 with the entry's own slug and n at least 1.", entry.Id);
        }

        return Result.Success;
    }

    /// <summary>
    /// The cross-entry duplicate pass, in the normative order: one iteration per entry, id before slug,
    /// so the reported id is the first entry that repeats either one.
    /// </summary>
    /// <param name="entries">The already value-validated entries, in file order.</param>
    public static ErrorOr<Success> ValidateCrossEntry(IReadOnlyList<VoiceCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var slugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!ids.Add(entry.Id) || !slugs.Add(entry.Slug))
            {
                return Invalid(CatalogInvalid, "Every voice id and every voice slug must be unique.", entry.Id);
            }
        }

        return Result.Success;
    }

    /// <summary>
    /// The last file-level rule: a catalog at revision 1 or later needs at least one active voice per
    /// language. A revision below 1 skips it entirely (the development placeholder, D9).
    /// </summary>
    /// <param name="revision">The file revision.</param>
    /// <param name="entries">The already validated entries.</param>
    public static ErrorOr<Success> ValidateLanguages(long revision, IReadOnlyList<VoiceCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (revision < 1)
        {
            return Result.Success;
        }

        foreach (var language in Languages)
        {
            if (!entries.Any(entry => entry.Active && string.Equals(entry.Language, language, StringComparison.Ordinal)))
            {
                return Invalid(CatalogInvalid, "A catalog at revision 1 or later needs at least one active voice per language.", null);
            }
        }

        return Result.Success;
    }

    /// <summary>
    /// Whether <paramref name="id"/> is a D9 id — the single implementation the 09 <c>VoiceId</c> value
    /// object reuses (CR-10): NFC, equal to its own <c>Trim()</c>, 1–64 runes, first rune a letter or a
    /// number, the rest a letter/number or <c>_</c>, space, <c>.</c>, <c>-</c>, and none of the alias
    /// markers (<c>-Female</c>, <c>-Male</c>, <c>:</c>, a <c>-V2</c> suffix).
    /// </summary>
    /// <param name="id">The candidate preset name.</param>
    public static bool IsValidId(string id)
    {
        if (id is null || id.Length == 0 || !id.IsNormalized(NormalizationForm.FormC))
        {
            return false;
        }

        if (!string.Equals(id, id.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        if (id.Contains(':', StringComparison.Ordinal)
            || id.Contains("-Female", StringComparison.Ordinal)
            || id.Contains("-Male", StringComparison.Ordinal)
            || id.EndsWith("-V2", StringComparison.Ordinal))
        {
            return false;
        }

        var runes = 0;
        foreach (var rune in id.EnumerateRunes())
        {
            runes++;
            if (runes > IdMaxRunes)
            {
                return false;
            }

            if (IsLetterOrNumber(rune))
            {
                continue;
            }

            if (runes == IdMinRunes || !IsSeparator(rune))
            {
                return false;
            }
        }

        return runes >= IdMinRunes;
    }

    /// <summary>
    /// Slugs an id (D9): NFD, combining marks dropped, <c>đ</c>/<c>Đ</c> folded to <c>d</c>, lower case,
    /// every run of non-<c>[a-z0-9]</c> collapsed to one <c>-</c>, then the <c>-</c> ends trimmed.
    /// <c>"Hải Đăng"</c> → <c>"hai-dang"</c>.
    /// </summary>
    /// <param name="id">The voice id.</param>
    public static string Slugify(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var decomposed = id.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (rune.Value is LatinSmallLetterDWithStroke or LatinCapitalLetterDWithStroke)
            {
                folded.Append('d');
                continue;
            }

            folded.Append(rune.ToString());
        }

        var lowered = folded.ToString().ToLowerInvariant();
        var slug = new StringBuilder(lowered.Length);
        foreach (var character in lowered)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != SeparatorHyphen)
            {
                slug.Append((char)SeparatorHyphen);
            }
        }

        if (slug.Length > 0 && slug[^1] == SeparatorHyphen)
        {
            slug.Length--;
        }

        return slug.ToString();
    }

    private static bool IsLetterOrNumber(Rune rune) =>
        LetterOrNumberCategories.Contains(Rune.GetUnicodeCategory(rune));

    private static bool IsSeparator(Rune rune) =>
        rune.Value is SeparatorUnderscore or SeparatorSpace or SeparatorDot or SeparatorHyphen;

    /// <summary>
    /// The length of <paramref name="value"/> in code points after Python's <c>str.strip()</c>. Python
    /// strips exactly the 29 <c>str.isspace()</c> code points, which include U+001C–U+001F — characters
    /// .NET's <see cref="string.Trim()"/> does <em>not</em> remove — so the keep is spelled out instead
    /// of relying on the BCL set (review R2). The stored display name is unchanged; only the length is
    /// measured this way.
    /// </summary>
    private static int PythonTrimmedRuneCount(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsPythonWhitespace(value[start]))
        {
            start++;
        }

        while (end > start && IsPythonWhitespace(value[end - 1]))
        {
            end--;
        }

        var count = 0;
        var span = value.AsSpan(start, end - start);
        foreach (var rune in span.EnumerateRunes())
        {
            count++;
            _ = rune;
        }

        return count;
    }

    /// <summary>The 29 code points Python's <c>str.isspace()</c> accepts (no surrogate pairs involved).</summary>
    private static bool IsPythonWhitespace(char character) => character switch
    {
        '\u0009' or '\u000a' or '\u000b' or '\u000c' or '\u000d' => true,
        '\u001c' or '\u001d' or '\u001e' or '\u001f' => true,
        '\u0020' or '\u0085' or '\u00a0' or '\u1680' => true,
        >= '\u2000' and <= '\u200a' => true,
        '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' => true,
        _ => false,
    };

    /// <summary>Whether <paramref name="commit"/> matches Python's <c>[0-9a-fA-F]{40}</c>.</summary>
    private static bool IsVieneuCommit(string? commit) =>
        commit is { Length: 40 }
        && commit.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>
    /// Whether the key matches Python's <c>voice-previews/([a-z0-9-]+)\.v(\d+)\.mp3</c> with the
    /// captured slug equal to the entry's own <paramref name="slug"/> and <c>int(version) &gt;= 1</c>:
    /// <c>\d</c> is a Unicode decimal digit, so <c>v01</c> and <c>v١٢</c> are both accepted, exactly as
    /// the proxy accepts them.
    /// </summary>
    private static bool IsValidPreviewStorageKey(string? key, string slug)
    {
        if (key is null)
        {
            return true;
        }

        // Python's captured slug class is ([a-z0-9-]+), so it needs at least one character: a voice
        // whose id slugifies to "" (a CJK id, say) cannot carry a preview key at all (review R1).
        if (slug.Length == 0 || !slug.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            return false;
        }

        var prefix = PreviewPrefix + slug + PreviewVersionSeparator;
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !key.EndsWith(PreviewSuffix, StringComparison.Ordinal)
            || key.Length <= prefix.Length + PreviewSuffix.Length)
        {
            return false;
        }

        var version = key.AsSpan(prefix.Length, key.Length - prefix.Length - PreviewSuffix.Length);
        var value = 0L;
        var large = false;

        foreach (var rune in version.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.DecimalDigitNumber)
            {
                return false;
            }

            var digit = (long)Rune.GetNumericValue(rune);
            if (large)
            {
                continue;
            }

            if (value > (long.MaxValue - digit) / 10)
            {
                large = true;
                continue;
            }

            value = (value * 10) + digit;
        }

        return large || value >= 1;
    }

    private static Error Invalid(string code, string description, string? voiceId) =>
        Error.Validation(code, description, new Dictionary<string, object> { [VoiceIdMetadataKey] = voiceId! });
}
