using System.Globalization;
using System.Text;
using NightTales.Application.Features.Research.Facts;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// The deterministic filter between the raw <c>facts.v1</c> model output and what a fact sheet may store
/// (02.5.3-T2, F12/F18, T1 hand-over 5). The C# <c>FactsV1</c> accepts any string and any array the strict
/// schema lets through, so this validator — not the schema — owns alias resolution, statement shape, control
/// characters, deduplication, the verbatim-copy block, the fact cap and id assignment.
/// </summary>
/// <remarks>
/// <para>
/// The rules run in the order of plan §3, each drop counted (never thrown): (1) resolve aliases to source
/// ids, dropping a fact with no known alias and keeping the known aliases of a partially-known one; (2)
/// reject a raw statement longer than four times the stored cap, then one carrying a NUL/DEL/other C0 control
/// character that is not whitespace, one carrying a bidi override or isolate control, or one carrying a lone
/// surrogate, then normalise (NFC + collapsed whitespace) and reject an empty or over-long one; (3)
/// deduplicate on the normalised statement plus the sorted resolved ids, first wins; (4) keep the first
/// <c>MaxFacts</c> survivors and drop a verbatim copy — the statement is scored against the whole
/// url-source corpus of the run, and a score above the overlap threshold, a score outside [0,1] or a cited
/// url source with no text is dropped (fail closed, P4/SEC-5).
/// </para>
/// <para>
/// Cost is bounded: at most <c>MaxFacts</c> statements are ever scored, and only the first
/// <c>MaxFacts × 4</c> inputs are examined, so a hostile or runaway output cannot drive a scoring call per
/// fact — the tail is counted as <c>over_cap</c> without scoring.
/// </para>
/// <para>
/// A null output or a null fact is not an error: it yields no facts. Zero surviving facts is a valid result
/// the job still persists as an empty draft (F13).
/// </para>
/// </remarks>
public static class FactSheetValidator
{
    /// <summary>The longest statement the validator keeps, after normalisation (rule 2).</summary>
    public const int StatementMaxChars = 600;

    /// <summary>
    /// The longest raw statement the validator scans (rule 2, P6): four times <see cref="StatementMaxChars"/>,
    /// so a statement that could never normalise into a kept one is rejected before the control-character scan
    /// and before NFC — whose canonical reordering a hostile run of combining marks could otherwise drive
    /// quadratic on the raw text.
    /// </summary>
    public const int RawStatementMaxChars = StatementMaxChars * 4;

    /// <summary>The DEL control character, rejected alongside the C0 controls (rule 2).</summary>
    private const char Delete = '\u007F';

    /// <summary>
    /// The bidi formatting controls the statement may not carry (rule 2, SEC-6): the embedding/override set
    /// <c>U+202A..U+202E</c>, the isolate set <c>U+2066..U+2069</c>, the marks <c>U+200E</c>/<c>U+200F</c> and
    /// the Arabic letter mark <c>U+061C</c>. They reorder how the stored fact renders for a reader while the
    /// bytes stay attacker-chosen, and a stored fact is later fed to the writer. The zero-width joiner and
    /// non-joiner (<c>U+200D</c>/<c>U+200C</c>) stay allowed: emoji sequences and Indic and Arabic scripts
    /// need them.
    /// </summary>
    private const string BidiControls = "\u061C\u200E\u200F\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069";

    /// <summary>
    /// Validates one extraction output against the run's context (see the class remarks for the ordered
    /// rules). The call is pure: it reads <paramref name="ctx"/> and <paramref name="scoreFact"/> and never
    /// mutates them, so the same input always yields the same result.
    /// </summary>
    /// <param name="output">The model output; its <c>Facts</c> may be null or hold null elements.</param>
    /// <param name="ctx">The run's alias map, id watermark, threshold, cap and source texts.</param>
    /// <param name="scoreFact">The 02.5.6 scorer adapter; returns the statement's overlap in [0,1] against the cited sources.</param>
    /// <returns>The surviving facts, the next free id, the drops and the model's fact count.</returns>
    public static FactSheetValidationResult Validate(
        FactsV1 output,
        FactSheetValidationContext ctx,
        Func<ScoreFactRequest, decimal> scoreFact)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(scoreFact);

        var drafts = output?.Facts;
        if (drafts is null || drafts.Count == 0)
        {
            return new FactSheetValidationResult([], ctx.MaxFactId, [], 0);
        }

        var modelFactCount = drafts.Count;
        var maxFacts = Math.Max(ctx.MaxFacts, 0);
        var scanLimit = ScanLimit(maxFacts);

        var kept = new List<FactItem>(Math.Min(modelFactCount, maxFacts));
        var drops = new List<FactDrop>();
        var seenStatements = new HashSet<string>(StringComparer.Ordinal);
        var corpus = Corpus(ctx.SourceTexts);
        var nextId = ctx.MaxFactId;

        for (var index = 0; index < modelFactCount; index++)
        {
            // Rule 5 (bounded cost): the tail beyond the scan window is counted, never scored.
            if (index >= scanLimit)
            {
                drops.Add(new FactDrop(FactDropReasons.OverCap, index));
                continue;
            }

            var draft = drafts[index];
            if (draft is null)
            {
                drops.Add(new FactDrop(FactDropReasons.UnknownAlias, index));
                continue;
            }

            // Rule 1: resolve every alias; a fact with no known alias cannot be sourced, so it is dropped.
            if (!TryResolveAliases(draft.SourceAliases, ctx.AliasToSourceId, out var aliases, out var sourceIds))
            {
                drops.Add(new FactDrop(FactDropReasons.UnknownAlias, index));
                continue;
            }

            var rawStatement = draft.Statement;

            // Rule 2 (P6): a raw statement that could never normalise into a kept one is rejected before the
            // scans and before NFC, whose canonical reordering hostile combining marks can make quadratic.
            if (rawStatement is { Length: > RawStatementMaxChars })
            {
                drops.Add(new FactDrop(FactDropReasons.StatementInvalid, index));
                continue;
            }

            // Rule 2: the control-character scan runs on the raw statement, before whitespace collapse.
            if (HasForbiddenControlCharacter(rawStatement))
            {
                drops.Add(new FactDrop(FactDropReasons.ControlCharactersInvalid, index));
                continue;
            }

            // Rule 2 (SEC-6): a bidi override or isolate control is dropped as an unusable statement.
            if (HasBidiControl(rawStatement))
            {
                drops.Add(new FactDrop(FactDropReasons.StatementInvalid, index));
                continue;
            }

            if (HasLoneSurrogate(rawStatement))
            {
                drops.Add(new FactDrop(FactDropReasons.StatementInvalid, index));
                continue;
            }

            var statement = CollapseWhitespace(NormalizeStatement(rawStatement));
            if (statement.Length == 0 || statement.Length > StatementMaxChars)
            {
                drops.Add(new FactDrop(FactDropReasons.StatementInvalid, index));
                continue;
            }

            // Rule 3: dedupe on the normalised statement plus the sorted resolved ids; the first wins.
            var key = DedupeKey(statement, sourceIds);
            if (!seenStatements.Add(key))
            {
                drops.Add(new FactDrop(FactDropReasons.Duplicate, index));
                continue;
            }

            // Rule 4 (P4): the cap is tested before the score, so a fact that would be dropped as over_cap
            // never pays for a scoring pass.
            if (kept.Count >= maxFacts)
            {
                drops.Add(new FactDrop(FactDropReasons.OverCap, index));
                continue;
            }

            // Rule 4 (SEC-5): a cited url source without text leaves nothing to score against, so the fact is
            // dropped fail closed instead of being stored on a score that proves nothing.
            if (!HasTextForEveryCitation(ctx.SourceTexts, sourceIds))
            {
                drops.Add(new FactDrop(FactDropReasons.VerbatimCopy, index));
                continue;
            }

            // Rule 4 (SEC-5/F18): the score is taken against the whole url-source corpus of the run, never
            // only the sources this fact cites, so a copy that cites a note alias or another source cannot
            // score zero and slip through. A score above the threshold — or outside [0,1] — fails closed.
            var overlap = scoreFact(new ScoreFactRequest(statement, corpus));
            if (IsVerbatimCopy(overlap, ctx.OverlapBlockThreshold))
            {
                drops.Add(new FactDrop(FactDropReasons.VerbatimCopy, index));
                continue;
            }

            // Rule 5: ids continue from the episode's watermark, in output order, and are never reused.
            nextId++;
            kept.Add(new FactItem(
                string.Concat("f", nextId.ToString(CultureInfo.InvariantCulture)),
                statement,
                sourceIds,
                aliases,
                overlap,
                draft.Confidence,
                FactOrigin.Model));
        }

        return new FactSheetValidationResult(kept, nextId, drops, modelFactCount);
    }

    /// <summary>Resolves the aliases of one fact to source ids, keeping the known aliases in citation order.</summary>
    private static bool TryResolveAliases(
        IReadOnlyList<string>? sourceAliases,
        IReadOnlyDictionary<string, Guid>? aliasToSourceId,
        out List<string> aliases,
        out List<Guid> sourceIds)
    {
        aliases = [];
        sourceIds = [];
        if (sourceAliases is null || aliasToSourceId is null)
        {
            return false;
        }

        var seenAliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in sourceAliases)
        {
            if (string.IsNullOrEmpty(alias) || !aliasToSourceId.TryGetValue(alias, out var sourceId))
            {
                continue; // unknown or empty alias
            }

            if (!seenAliases.Add(alias))
            {
                continue; // a repeated alias is kept once
            }

            aliases.Add(alias);
            sourceIds.Add(sourceId);
        }

        return aliases.Count > 0;
    }

    /// <summary>
    /// Rule 4's corpus: the text of every url source of the run's plan, in id order, so one statement's score
    /// never depends on which source the model chose to cite (SEC-5). The user note is not part of the plan
    /// this map is built from and a blank text contributes nothing (F18).
    /// </summary>
    private static List<SourceText> Corpus(IReadOnlyDictionary<Guid, SourceText>? sourceTexts)
    {
        var corpus = new List<SourceText>(sourceTexts?.Count ?? 0);
        if (sourceTexts is null)
        {
            return corpus;
        }

        foreach (var source in sourceTexts.Values)
        {
            if (!string.IsNullOrWhiteSpace(source.Text))
            {
                corpus.Add(source);
            }
        }

        // A dictionary does not promise an order; the score must not depend on one.
        corpus.Sort(static (left, right) => left.SourceId.CompareTo(right.SourceId));

        return corpus;
    }

    /// <summary>
    /// Whether every cited source can be checked: a cited id the run's plan carries no text for is the user
    /// note, which is kept on purpose (F18), while a cited url source whose text is missing or blank gives the
    /// scorer nothing to compare against, so the fact is dropped fail closed (SEC-5).
    /// </summary>
    private static bool HasTextForEveryCitation(
        IReadOnlyDictionary<Guid, SourceText>? sourceTexts,
        List<Guid> sourceIds)
    {
        if (sourceTexts is null)
        {
            return true;
        }

        foreach (var sourceId in sourceIds)
        {
            if (sourceTexts.TryGetValue(sourceId, out var source) && string.IsNullOrWhiteSpace(source.Text))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The dedupe key: the normalised statement plus the sorted resolved ids (statement cannot hold NUL).</summary>
    private static string DedupeKey(string statement, List<Guid> sourceIds)
    {
        var ids = sourceIds.OrderBy(id => id).Select(id => id.ToString("D"));
        return string.Concat(statement, "\0", string.Join(',', ids));
    }

    /// <summary>Whether the score means "a copy": above the threshold, or outside [0,1] and so untrusted.</summary>
    private static bool IsVerbatimCopy(decimal overlap, decimal threshold) =>
        overlap > threshold || overlap < 0m || overlap > 1m;

    /// <summary>The bounded scan window: <c>MaxFacts × 4</c>, or zero when the cap is not positive.</summary>
    private static int ScanLimit(int maxFacts) =>
        maxFacts <= 0 ? 0 : (int)Math.Min((long)maxFacts * 4L, int.MaxValue);

    /// <summary>NFC-normalises the statement; safe because lone surrogates are rejected first.</summary>
    private static string NormalizeStatement(string? statement) =>
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
