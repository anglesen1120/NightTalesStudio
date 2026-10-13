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
/// normalise the statement (NFC + collapsed whitespace) and reject an empty or over-long one, a lone
/// surrogate and any NUL/DEL/other C0 control character that is not whitespace; (3) deduplicate on the
/// normalised statement plus the sorted resolved ids, first wins; (4) drop a verbatim copy (a score above the
/// overlap threshold, or a score outside [0,1] — fail closed); (5) keep the first <c>MaxFacts</c>.
/// </para>
/// <para>
/// Cost is bounded: only the first <c>MaxFacts × 4</c> inputs are examined, so a hostile or runaway output
/// cannot drive a scoring call per fact — the tail is counted as <c>over_cap</c> without scoring.
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

    /// <summary>The DEL control character, rejected alongside the C0 controls (rule 2).</summary>
    private const char Delete = '\u007F';

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

            // Rule 2: the control-character scan runs on the raw statement, before whitespace collapse.
            if (HasForbiddenControlCharacter(draft.Statement))
            {
                drops.Add(new FactDrop(FactDropReasons.ControlCharactersInvalid, index));
                continue;
            }

            if (HasLoneSurrogate(draft.Statement))
            {
                drops.Add(new FactDrop(FactDropReasons.StatementInvalid, index));
                continue;
            }

            var statement = CollapseWhitespace(NormalizeStatement(draft.Statement));
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

            // Rule 4: a verbatim copy (or a score outside [0,1]) is dropped, fail closed.
            var overlap = scoreFact(new ScoreFactRequest(statement, Cite(ctx.SourceTexts, sourceIds)));
            if (IsVerbatimCopy(overlap, ctx.OverlapBlockThreshold))
            {
                drops.Add(new FactDrop(FactDropReasons.VerbatimCopy, index));
                continue;
            }

            // Rule 5: only the first MaxFacts survivors are kept.
            if (kept.Count >= maxFacts)
            {
                drops.Add(new FactDrop(FactDropReasons.OverCap, index));
                continue;
            }

            // Rule 6: ids continue from the episode's watermark, in output order, and are never reused.
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

    /// <summary>Collects the text of the cited sources; an id without text is scored against nothing.</summary>
    private static List<SourceText> Cite(
        IReadOnlyDictionary<Guid, SourceText>? sourceTexts,
        List<Guid> sourceIds)
    {
        var cited = new List<SourceText>(sourceIds.Count);
        if (sourceTexts is null)
        {
            return cited;
        }

        foreach (var sourceId in sourceIds)
        {
            if (sourceTexts.TryGetValue(sourceId, out var source))
            {
                cited.Add(source);
            }
        }

        return cited;
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
