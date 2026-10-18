#pragma warning disable CA1716 // `Structure` is the planned namespace of the variety guard (02.8.4-T1 plan); the .NET-only API is never consumed from Visual Basic.
using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Domain.Stories.Structure;

/// <summary>
/// The pure variety guard of the idea generator (02.8.4-T1, 02.8.1 plan §3.5): it reads the series' approved
/// structure history, steers the idea prompt, blocks a candidate that repeats a recent episode, and checks a
/// written story against the plan it was written under.
/// </summary>
/// <remarks>
/// <para>
/// No I/O, no clock, no configuration lookup: the history and the knobs arrive as arguments, which is what lets
/// the whole rule set be unit tested and lets the planner call <see cref="AssessBatch"/> once per run.
/// </para>
/// <para>
/// The rules are R1 (triple repeat within <c>WindowK</c>) and R3 (template repeat within <c>TemplateWindowK</c>)
/// as hard blocks, R2 (tag share cap) as a soft penalty, R4 (rotation) as steering, and R5 (cold start) as the
/// exemption that keeps a young series proposable. <c>narrator_mode</c> is never a dimension.
/// </para>
/// <para>
/// The guard compares the stored D3 tag strings exactly as they were written (ordinal, trimmed); it never reads
/// story text and logs no content (D19). Values prefixed <c>other:</c> are counted and compared as-is but are
/// never suggested as required or forbidden, because they are not stable across episodes.
/// </para>
/// </remarks>
public static class VarietyGuard
{
    /// <summary>The <c>sub_genre</c> structure dimension (README D3).</summary>
    public const string DimensionSubGenre = "sub_genre";

    /// <summary>The <c>setting</c> structure dimension (README D3, free text).</summary>
    public const string DimensionSetting = "setting";

    /// <summary>The <c>opening</c> structure dimension (README D3).</summary>
    public const string DimensionOpening = "opening";

    /// <summary>The <c>twist_type</c> structure dimension (README D3).</summary>
    public const string DimensionTwistType = "twist_type";

    /// <summary>The issue rule of a written structure tag the plan forbids; the 02.8.2 gate fails on it.</summary>
    public const string IssueForbiddenUsed = "structure_plan_forbidden_used";

    /// <summary>The issue rule of a required structure tag the writer did not use; advisory only.</summary>
    public const string IssueRequiredMissing = "structure_plan_missing";

    /// <summary>The prefix the D3 mapper gives an unmapped tag value.</summary>
    private const string OtherPrefix = "other:";

    /// <summary>R5: at least this many structured episodes must be in the window before any rule applies.</summary>
    private const int ColdStartMinimum = 2;

    /// <summary>R2 with one violating dimension.</summary>
    private const decimal SingleViolationPenalty = 0.5m;

    /// <summary>R2 with two or more violating dimensions: capped so a soft hit never equals a hard block.</summary>
    private const decimal MultipleViolationPenalty = 0.75m;

    /// <summary>
    /// Reads the series' structure history once for a whole idea batch (02.8.1 plan §4 U1 step 2): the window
    /// counts R1, R2 and R4 need, the recent entries R1/R3 compare against, and the steering the prompt receives.
    /// </summary>
    /// <remarks>
    /// The window is the first <see cref="VarietyOptions.WindowK"/> entries of <paramref name="history"/> (it is
    /// newest first) and the template window its first <see cref="VarietyOptions.TemplateWindowK"/> entries. On a
    /// cold start — fewer than two window entries carrying a structure — every count, entry list and suggestion is
    /// empty, so <see cref="Evaluate"/> can neither reject nor penalise.
    /// </remarks>
    /// <param name="history">The series' approved episodes, newest first; any length, empty for a new series.</param>
    /// <param name="opt">The guard's knobs.</param>
    /// <returns>The batch assessment <see cref="Evaluate"/> and <see cref="ToPlan"/> consume.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> or <paramref name="opt"/> is <see langword="null"/>.</exception>
    public static VarietyBatchAssessment AssessBatch(IReadOnlyList<EpisodeStructureRef> history, VarietyOptions opt)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(opt);

        List<EpisodeStructureRef> window = Window(history, opt.WindowK);
        List<EpisodeStructureRef> lastFive = Window(history, opt.TemplateWindowK);

        if (StructuredCount(window) < ColdStartMinimum)
        {
            // R5: a new series must not be blocked, and there is nothing to steer away from yet.
            return new VarietyBatchAssessment(
                window.Count,
                NoCounts(),
                NoCounts(),
                NoCounts(),
                NoCounts(),
                [],
                [],
                StructureRequirements.None,
                StructureForbidden.None);
        }

        Dictionary<string, int> subGenre = Count(window, static tags => tags.SubGenre);
        Dictionary<string, int> setting = Count(window, static tags => tags.Setting);
        Dictionary<string, int> opening = Count(window, static tags => tags.Opening);
        Dictionary<string, int> twist = Count(window, static tags => tags.TwistType);

        List<string> forbiddenSubGenre = OverCap(subGenre, window.Count, opt.TagShareMax);
        List<string> forbiddenSetting = OverCap(setting, window.Count, opt.TagShareMax);
        List<string> forbiddenOpening = OverCap(opening, window.Count, opt.TagShareMax);
        List<string> forbiddenTwist = OverCap(twist, window.Count, opt.TagShareMax);

        var required = new StructureRequirements(
            Rotation(subGenre, forbiddenSubGenre, opt.RotationMinCount),
            Rotation(setting, forbiddenSetting, opt.RotationMinCount),
            Rotation(opening, forbiddenOpening, opt.RotationMinCount),
            Rotation(twist, forbiddenTwist, opt.RotationMinCount));

        return new VarietyBatchAssessment(
            window.Count,
            subGenre,
            setting,
            opening,
            twist,
            window,
            lastFive,
            required,
            new StructureForbidden(forbiddenSubGenre, forbiddenSetting, forbiddenOpening, forbiddenTwist));
    }

    /// <summary>
    /// Judges one candidate against the batch assessment (02.8.4-T1 R1–R4): a hard-block reason, the R2 penalty,
    /// and the soft violations that widen the batch's forbidden set.
    /// </summary>
    /// <remarks>
    /// R3 (template repeat) wins over R1 (triple repeat) when both fire, because it is the more specific match. A
    /// blocked candidate still carries its soft violations. Nothing is rejected and nothing is penalised when
    /// <see cref="VarietyOptions.Enabled"/> is <see langword="false"/> — the steering of
    /// <see cref="AssessBatch"/> is unaffected by the switch.
    /// </remarks>
    /// <param name="candidate">The proposed idea.</param>
    /// <param name="batch">The assessment of the current history.</param>
    /// <param name="opt">The guard's knobs.</param>
    /// <returns>The decision; <see cref="VarietyDecision.None"/> for a clean, disabled or cold-start candidate.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static VarietyDecision Evaluate(IdeaCandidate candidate, VarietyBatchAssessment batch, VarietyOptions opt)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(opt);

        if (!opt.Enabled)
        {
            return VarietyDecision.None;
        }

        List<string> soft = SoftViolations(candidate, batch);
        string? reject = TemplateRepeat(candidate, batch)
            ? IdeaRejectionReason.VarietyTemplateRepeat
            : TripleRepeat(candidate, batch)
                ? IdeaRejectionReason.VarietyTripleRepeat
                : null;

        decimal penalty = soft.Count switch
        {
            0 => 0m,
            1 => SingleViolationPenalty,
            _ => MultipleViolationPenalty,
        };

        return reject is null && penalty == 0m
            ? VarietyDecision.None
            : new VarietyDecision(penalty, reject, soft);
    }

    /// <summary>
    /// Checks a written story's structure against the plan it was written under (02.8.4-T1, called by the 02.8.2
    /// gate): a forbidden tag is a gate failure, a missing required tag an advisory.
    /// </summary>
    /// <remarks>
    /// Only the four structure dimensions are checked; <c>narrator_mode</c> is ignored because the preset owns it.
    /// A dimension can report both issues at once. The returned <see cref="VarietyIssue.Detail"/> carries the tag
    /// value at fault — structure metadata, never story text — and a caller that logs records counts and rule ids
    /// only (D19/D13).
    /// </remarks>
    /// <param name="written">The structure tags the writer produced.</param>
    /// <param name="plan">The plan the story was written under.</param>
    /// <returns>One issue per violated rule and dimension, in the fixed dimension order; empty when the plan holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="written"/> or <paramref name="plan"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<VarietyIssue> VerifyWritten(StoryStructureTags written, StructurePlan plan)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(plan);

        var issues = new List<VarietyIssue>(4);
        Check(DimensionSubGenre, Normalize(written.SubGenre), plan.Forbidden.SubGenre, plan.Required.SubGenre, issues);
        Check(DimensionSetting, Normalize(written.Setting), plan.Forbidden.Setting, plan.Required.Setting, issues);
        Check(DimensionOpening, Normalize(written.Opening), plan.Forbidden.Opening, plan.Required.Opening, issues);
        Check(DimensionTwistType, Normalize(written.TwistType), plan.Forbidden.TwistType, plan.Required.TwistType, issues);
        return issues;
    }

    /// <summary>
    /// Builds the plan the writer receives (02.8.4-T1, README D6 <c>structure_plan</c>): the batch's steering
    /// required values, and the batch's over-cap values unioned with the ones this candidate already violated.
    /// </summary>
    /// <remarks>
    /// Merging the candidate's own soft violations into the forbidden arrays is what stops the writer from
    /// reusing a saturated value the candidate happened to pick: the plan is derived from the history at
    /// generation time, so it is built here again rather than persisted on the idea.
    /// </remarks>
    /// <param name="batch">The assessment of the current history.</param>
    /// <param name="decision">The decision taken for the picked idea.</param>
    /// <returns>The plan; <see cref="StructureRequirements.None"/>/<see cref="StructureForbidden.None"/> pieces when nothing is steered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> or <paramref name="decision"/> is <see langword="null"/>.</exception>
    public static StructurePlan ToPlan(VarietyBatchAssessment batch, VarietyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(decision);

        var violated = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string violation in decision.SoftViolations)
        {
            int separator = violation.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0) continue;

            string dimension = violation[..separator];
            string value = violation[(separator + 1)..];
            if (value.Length == 0) continue;

            if (!violated.TryGetValue(dimension, out List<string>? values))
            {
                values = [];
                violated[dimension] = values;
            }

            values.Add(value);
        }

        return new StructurePlan(
            batch.SuggestedRequired,
            new StructureForbidden(
                Union(batch.SuggestedForbidden.SubGenre, Values(violated, DimensionSubGenre)),
                Union(batch.SuggestedForbidden.Setting, Values(violated, DimensionSetting)),
                Union(batch.SuggestedForbidden.Opening, Values(violated, DimensionOpening)),
                Union(batch.SuggestedForbidden.TwistType, Values(violated, DimensionTwistType))));
    }

    /// <summary>The window: the first <paramref name="size"/> entries of a newest-first history.</summary>
    private static List<EpisodeStructureRef> Window(IReadOnlyList<EpisodeStructureRef> history, int size)
    {
        if (size <= 0 || history.Count == 0)
        {
            return [];
        }

        if (size >= history.Count)
        {
            return [.. history];
        }

        var window = new List<EpisodeStructureRef>(size);
        for (int index = 0; index < size; index++)
        {
            window.Add(history[index]);
        }

        return window;
    }

    /// <summary>Counts the window entries that carry any structure at all (R5's denominator).</summary>
    private static int StructuredCount(IReadOnlyList<EpisodeStructureRef> entries)
    {
        int count = 0;
        foreach (EpisodeStructureRef entry in entries)
        {
            if (HasStructure(entry.Structure))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether the tags carry any structure (mirrors the history reader's own filter).</summary>
    private static bool HasStructure(StoryStructureTags tags) =>
        tags.Beats is { Count: > 0 }
        || Normalize(tags.SubGenre).Length > 0
        || Normalize(tags.Setting).Length > 0
        || Normalize(tags.Opening).Length > 0
        || Normalize(tags.TwistType).Length > 0;

    /// <summary>Counts the non-empty values one dimension takes across the window.</summary>
    private static Dictionary<string, int> Count(
        IReadOnlyList<EpisodeStructureRef> window,
        Func<StoryStructureTags, string> selector)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (EpisodeStructureRef entry in window)
        {
            string value = Normalize(selector(entry.Structure));
            if (value.Length == 0)
            {
                continue;
            }

            counts[value] = counts.TryGetValue(value, out int seen) ? seen + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// R2: the values a candidate would push above the share cap, ordinal sorted; unmapped <c>other:</c> values
    /// are counted but never suggested.
    /// </summary>
    private static List<string> OverCap(IReadOnlyDictionary<string, int> counts, int windowSize, decimal tagShareMax)
    {
        var over = new List<string>();
        foreach ((string value, int seen) in counts)
        {
            if (IsOther(value))
            {
                continue;
            }

            decimal share = (decimal)(seen + 1) / (windowSize + 1);
            if (share > tagShareMax)
            {
                over.Add(value);
            }
        }

        over.Sort(StringComparer.Ordinal);
        return over;
    }

    /// <summary>
    /// R4: the least-used unmapped value below the rotation floor, tie-broken by ordinal value; a value already
    /// forbidden is never required.
    /// </summary>
    private static string? Rotation(
        IReadOnlyDictionary<string, int> counts,
        IReadOnlyList<string> forbidden,
        int rotationMinCount)
    {
        string? best = null;
        int bestCount = 0;

        foreach ((string value, int seen) in counts)
        {
            if (seen >= rotationMinCount || IsOther(value) || IsForbidden(value, forbidden))
            {
                continue;
            }

            if (best is null || seen < bestCount || (seen == bestCount && string.CompareOrdinal(value, best) < 0))
            {
                best = value;
                bestCount = seen;
            }
        }

        return best;
    }

    /// <summary>R2 for one candidate: the <c>"&lt;dimension&gt;:&lt;value&gt;"</c> entries it violates.</summary>
    private static List<string> SoftViolations(IdeaCandidate candidate, VarietyBatchAssessment batch)
    {
        var violations = new List<string>(4);
        StoryStructureTags tags = candidate.Structure;
        AddViolation(DimensionSubGenre, Normalize(tags.SubGenre), batch.SuggestedForbidden.SubGenre, violations);
        AddViolation(DimensionSetting, Normalize(tags.Setting), batch.SuggestedForbidden.Setting, violations);
        AddViolation(DimensionOpening, Normalize(tags.Opening), batch.SuggestedForbidden.Opening, violations);
        AddViolation(DimensionTwistType, Normalize(tags.TwistType), batch.SuggestedForbidden.TwistType, violations);
        return violations;
    }

    /// <summary>Appends one R2 violation when the candidate's value is one the plan forbids.</summary>
    private static void AddViolation(
        string dimension,
        string value,
        IReadOnlyList<string> forbidden,
        List<string> violations)
    {
        if (value.Length > 0 && IsForbidden(value, forbidden))
        {
            violations.Add($"{dimension}:{value}");
        }
    }

    /// <summary>R3: the candidate repeats a recent template — signature, opening and twist-type all equal.</summary>
    private static bool TemplateRepeat(IdeaCandidate candidate, VarietyBatchAssessment batch)
    {
        string signature = Normalize(IdeaStructure.Signature(candidate.Beats ?? []));
        string opening = Normalize(candidate.Structure.Opening);
        string twist = Normalize(candidate.Structure.TwistType);

        if (signature.Length == 0 || opening.Length == 0 || twist.Length == 0)
        {
            return false;
        }

        foreach (EpisodeStructureRef entry in batch.LastFive)
        {
            if (entry.BeatSignature.Length == 0)
            {
                continue;
            }

            if (string.Equals(Normalize(entry.BeatSignature), signature, StringComparison.Ordinal)
                && string.Equals(Normalize(entry.Structure.Opening), opening, StringComparison.Ordinal)
                && string.Equals(Normalize(entry.Structure.TwistType), twist, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>R1: the candidate repeats the (sub_genre, opening, twist_type) triple of a window entry.</summary>
    private static bool TripleRepeat(IdeaCandidate candidate, VarietyBatchAssessment batch)
    {
        string subGenre = Normalize(candidate.Structure.SubGenre);
        string opening = Normalize(candidate.Structure.Opening);
        string twist = Normalize(candidate.Structure.TwistType);

        if (subGenre.Length == 0 || opening.Length == 0 || twist.Length == 0)
        {
            return false;
        }

        foreach (EpisodeStructureRef entry in batch.Recent)
        {
            if (string.Equals(Normalize(entry.Structure.SubGenre), subGenre, StringComparison.Ordinal)
                && string.Equals(Normalize(entry.Structure.Opening), opening, StringComparison.Ordinal)
                && string.Equals(Normalize(entry.Structure.TwistType), twist, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Appends the issues one written dimension raises.</summary>
    private static void Check(
        string dimension,
        string value,
        IReadOnlyList<string> forbidden,
        string? required,
        List<VarietyIssue> issues)
    {
        if (value.Length > 0 && IsForbidden(value, forbidden))
        {
            issues.Add(new VarietyIssue(IssueForbiddenUsed, dimension, value));
        }

        string requiredValue = Normalize(required);
        if (requiredValue.Length > 0 && !string.Equals(value, requiredValue, StringComparison.Ordinal))
        {
            issues.Add(new VarietyIssue(IssueRequiredMissing, dimension, requiredValue));
        }
    }

    /// <summary>The distinct union of a forbidden list and the values one dimension additionally violated.</summary>
    private static List<string> Union(IReadOnlyList<string> forbidden, IReadOnlyList<string> violated)
    {
        var union = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string value in forbidden)
        {
            union.Add(value);
        }

        foreach (string value in violated)
        {
            union.Add(value);
        }

        return [.. union];
    }

    /// <summary>The values the decision named for one dimension.</summary>
    private static List<string> Values(Dictionary<string, List<string>> violated, string dimension) =>
        violated.TryGetValue(dimension, out List<string>? values) ? values : [];

    /// <summary>Whether a value is already on a forbidden list.</summary>
    private static bool IsForbidden(string value, IReadOnlyList<string> forbidden) =>
        forbidden is { Count: > 0 } && forbidden.Contains(value, StringComparer.Ordinal);

    /// <summary>Whether the D3 mapper marked the value as unmapped (<c>other:&lt;text&gt;</c>).</summary>
    private static bool IsOther(string value) => value.StartsWith(OtherPrefix, StringComparison.Ordinal);

    /// <summary>A value as the guard compares it: trimmed, ordinal; a missing value is the empty string.</summary>
    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>A fresh empty count map (never shared, so no caller can mutate it through the record).</summary>
    private static Dictionary<string, int> NoCounts() => new(StringComparer.Ordinal);
}
