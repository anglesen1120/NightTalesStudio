using System.Text;

namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// The statement rules of a stored fact (02.5.3-T2 rule 2, 02.5.4-T1 decision 12): the deterministic shape
/// every fact statement must have before it is stored, shared by the extraction validator that filters the
/// model output (<see cref="FactSheetValidator"/>) and the edit path that re-validates what the owner typed
/// (<c>UpdateFactSheetCommandHandler</c>), so both halves of the feature accept exactly the same statements.
/// </summary>
/// <remarks>
/// <para>
/// The rules run in the validator's own order and each failure names the validator's drop reason
/// (<see cref="FactDropReasons"/>), so an edit that is refused and an extraction that is dropped are refused
/// for the same reason: (1) a raw statement longer than <see cref="RawStatementMaxChars"/> is rejected before
/// the scans and before NFC — whose canonical reordering a hostile run of combining marks could otherwise
/// drive quadratic on the raw text (P6); (2) a NUL, DEL or other non-whitespace C0 control character; (3) a
/// bidi override or isolate control (SEC-6); (4) a lone UTF-16 surrogate; (5) the NFC-normalised, whitespace
/// collapsed statement must be non-empty and at most <see cref="StatementMaxChars"/> characters.
/// </para>
/// <para>
/// <see cref="TryNormalize"/> is pure and never throws: it returns the reason string, never an
/// <c>ErrorOr</c>, because the caller owns the code and the field path the client sees (the validator counts
/// a drop, the edit returns 400 <c>fact_sheet_invalid_content</c> with <c>facts[&lt;i&gt;].statement</c>).
/// </para>
/// </remarks>
public static class FactStatementRules
{
    /// <summary>The longest statement that survives normalisation (the validator's rule 2 cap).</summary>
    public const int StatementMaxChars = 600;

    /// <summary>
    /// The longest raw statement that is scanned at all: four times <see cref="StatementMaxChars"/>, so a
    /// statement that could never normalise into a kept one is rejected before the control-character scan and
    /// before NFC.
    /// </summary>
    public const int RawStatementMaxChars = StatementMaxChars * 4;

    /// <summary>The DEL control character, rejected alongside the C0 controls.</summary>
    private const char Delete = '\u007F';

    /// <summary>
    /// The bidi formatting controls the statement may not carry (SEC-6): the embedding/override set
    /// <c>U+202A..U+202E</c>, the isolate set <c>U+2066..U+2069</c>, the marks <c>U+200E</c>/<c>U+200F</c> and
    /// the Arabic letter mark <c>U+061C</c>. They reorder how the stored fact renders for a reader while the
    /// bytes stay attacker-chosen, and a stored fact is later fed to the writer. The zero-width joiner and
    /// non-joiner (<c>U+200D</c>/<c>U+200C</c>) stay allowed: emoji sequences and Indic and Arabic scripts
    /// need them.
    /// </summary>
    private const string BidiControls = "\u061C\u200E\u200F\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069";

    /// <summary>
    /// Applies the shared statement rules to one raw statement and returns the stored form.
    /// </summary>
    /// <param name="statement">The raw statement, as the model produced it or the owner typed it; may be null.</param>
    /// <param name="normalized">
    /// The NFC-normalised, whitespace-collapsed statement when the call succeeds; otherwise the empty string.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when <paramref name="statement"/> is a storable statement, otherwise the
    /// <see cref="FactDropReasons"/> constant naming the rule it failed.
    /// </returns>
    public static string? TryNormalize(string? statement, out string normalized)
    {
        normalized = string.Empty;

        // Rule 2 (P6): a raw statement that could never normalise into a kept one is rejected before the
        // scans and before NFC, whose canonical reordering hostile combining marks can make quadratic.
        if (statement is { Length: > RawStatementMaxChars })
        {
            return FactDropReasons.StatementInvalid;
        }

        // The control-character scan runs on the raw statement, before whitespace collapse.
        if (HasForbiddenControlCharacter(statement))
        {
            return FactDropReasons.ControlCharactersInvalid;
        }

        if (HasBidiControl(statement))
        {
            return FactDropReasons.StatementInvalid;
        }

        if (HasLoneSurrogate(statement))
        {
            return FactDropReasons.StatementInvalid;
        }

        var collapsed = CollapseWhitespace(Normalize(statement));
        if (collapsed.Length == 0 || collapsed.Length > StatementMaxChars)
        {
            return FactDropReasons.StatementInvalid;
        }

        normalized = collapsed;
        return null;
    }

    /// <summary>NFC-normalises the statement; safe because lone surrogates are rejected first.</summary>
    private static string Normalize(string? statement) =>
        string.IsNullOrEmpty(statement) ? string.Empty : statement.Normalize(NormalizationForm.FormC);

    /// <summary>Collapses every run of whitespace to a single space and trims the ends.</summary>
    private static string CollapseWhitespace(string statement)
    {
        if (statement.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(statement.Length);
        var pendingSpace = false;
        foreach (var character in statement)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>Whether the raw statement carries NUL, DEL or another C0 control character that is not whitespace.</summary>
    private static bool HasForbiddenControlCharacter(string? statement)
    {
        if (string.IsNullOrEmpty(statement))
        {
            return false;
        }

        foreach (var character in statement)
        {
            if (character == Delete || (character < ' ' && !char.IsWhiteSpace(character)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the raw statement carries a bidi override or isolate control (<see cref="BidiControls"/>): it
    /// would reorder the stored fact for a reader while the bytes stay attacker-chosen (SEC-6). ZWJ/ZWNJ pass.
    /// </summary>
    private static bool HasBidiControl(string? statement)
    {
        if (string.IsNullOrEmpty(statement))
        {
            return false;
        }

        foreach (var character in statement)
        {
            if (BidiControls.Contains(character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the statement holds a UTF-16 surrogate that is not part of a valid pair.</summary>
    private static bool HasLoneSurrogate(string? statement)
    {
        if (string.IsNullOrEmpty(statement))
        {
            return false;
        }

        for (var index = 0; index < statement.Length; index++)
        {
            var character = statement[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < statement.Length && char.IsLowSurrogate(statement[index + 1]))
                {
                    index++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(character))
            {
                return true;
            }
        }

        return false;
    }
}
