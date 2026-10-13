using System.Globalization;
using System.Text;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Research.Facts;

/// <summary>The kind of a fact-extraction source (<c>story_sources.kind</c>, 02.5.2).</summary>
public enum FactSourceKind
{
    /// <summary>A fetched URL source; its text is <c>extracted_text</c>, and it may be truncated.</summary>
    Url = 0,

    /// <summary>The user's own note; its text is <c>note_text</c>, and it is never truncated (README CR-4).</summary>
    UserNote = 1,
}

/// <summary>
/// One candidate source handed to <see cref="FactInputBudget.Plan"/>: what the alias assignment needs and
/// nothing else. The caller fills <see cref="Text"/> from the row's own column of its kind —
/// <c>extracted_text</c> for a url, <c>note_text</c> for a user note.
/// </summary>
/// <param name="SourceId">The <c>story_sources.id</c>; also the tie-break of the creation-order sort.</param>
/// <param name="Title">The source title.</param>
/// <param name="Kind">Url or user note; a user note is never truncated.</param>
/// <param name="Text">The source text, or <see langword="null"/> when the row carries none.</param>
/// <param name="CreatedAt">The row's <c>created_at</c>; sources are ordered by it, ascending.</param>
public sealed record FactSourceCandidate(
    Guid SourceId,
    string Title,
    FactSourceKind Kind,
    string? Text,
    DateTimeOffset CreatedAt);

/// <summary>The input bounds of a fact-extraction call, bound from the <c>Facts</c> section (02.5.3-T1).</summary>
/// <param name="MaxSources">How many sources may be sent; the alias scheme allows at most <see cref="MaxSourcesCeiling"/>.</param>
/// <param name="PerSourceCharCap">The per-source character cap applied before the total budget.</param>
/// <param name="MaxInputTokens">The estimated-token ceiling of the serialised data object.</param>
public sealed record FactInputLimits(int MaxSources, int PerSourceCharCap, int MaxInputTokens)
{
    /// <summary>The largest source count the alias scheme allows (<c>s1…s10</c>); never alias <c>s11</c>.</summary>
    public const int MaxSourcesCeiling = 10;

    /// <summary>The default per-source character cap (<c>Facts:PerSourceCharCap</c>).</summary>
    public const int DefaultPerSourceCharCap = 20_000;

    /// <summary>The default total input budget in estimated tokens (<c>Facts:MaxInputTokens</c>).</summary>
    public const int DefaultMaxInputTokens = 60_000;

    /// <summary>The floor a per-source cap is halved to before the total-budget loop gives up.</summary>
    public const int MinimumPerSourceCharCap = 1_000;
}

/// <summary>Whether a <see cref="FactInputPlan"/> fits its total budget.</summary>
public enum FactInputBudgetStatus
{
    /// <summary>The estimated input is at or under <c>Facts:MaxInputTokens</c>; the plan may be sent.</summary>
    WithinBudget = 0,

    /// <summary>
    /// Even at the minimum per-source floor the estimated input is over the budget (only user notes or
    /// floor-truncated sources remain). The plan MUST NOT be sent: the caller fails the run instead of
    /// silently exceeding the cap (02.5.3-T1 failure handling).
    /// </summary>
    OverBudget = 1,
}

/// <summary>
/// The deterministic result of <see cref="FactInputBudget.Plan"/>: the sources the model may read, what was
/// dropped, what was truncated and the estimated input size.
/// </summary>
/// <param name="Sources">The sources, alias-assigned <c>s1…sN</c>, in creation order; never empty.</param>
/// <param name="ExcludedSourceIds">The ids of the sources the plan dropped, whether for empty text or for the <c>MaxSources</c> ceiling; reported, not an error.</param>
/// <param name="TruncatedAliases">The aliases whose text was cut; the <c>input_truncated</c> signal of README CR-4 for 02.5.3-T2 and the UI.</param>
/// <param name="EstimatedTokens">The estimated token count of the serialised data object of <paramref name="Sources"/>.</param>
/// <param name="Status">Whether the estimate fits <c>Facts:MaxInputTokens</c>.</param>
public sealed record FactInputPlan(
    IReadOnlyList<FactSourceInput> Sources,
    IReadOnlyList<Guid> ExcludedSourceIds,
    IReadOnlyList<string> TruncatedAliases,
    int EstimatedTokens,
    FactInputBudgetStatus Status)
{
    /// <summary>Whether the plan may be sent (<c>input_truncated</c> may still be true — a cut source fits).</summary>
    public bool WithinBudget => Status == FactInputBudgetStatus.WithinBudget;

    /// <summary>The README CR-4 <c>input_truncated</c> signal: at least one source text was cut.</summary>
    public bool InputTruncated => TruncatedAliases.Count > 0;
}

/// <summary>
/// The deterministic input budget of a fact-extraction call (02 README D6/D21, 02.5.3-T1): it selects and
/// alias-assigns the sources, truncates each one over its per-source cap, and shrinks the largest truncatable
/// sources until the serialised data object fits <c>Facts:MaxInputTokens</c>.
/// </summary>
/// <remarks>
/// <para>
/// It is pure and clock-free: the same candidates, limits and language always yield the same
/// <see cref="FactInputPlan"/>, so the provider prompt cache and the retry stay stable. The estimate is the
/// UTF-8 byte count of the very object <see cref="FactDataObjectJson.Serialize"/> produces, divided by four
/// and rounded up, so the estimate equals what the extractor sends.
/// </para>
/// <para>
/// Truncation keeps the head 60 % and the tail 40 % of a cut source around a
/// <c>\n[…truncated…]\n</c> marker, cut on text elements so a surrogate pair or a combining sequence is
/// never split. A <c>user_note</c> source is never truncated and always included; when only such sources (or
/// floor-truncated ones) are left and the budget still does not fit, the plan is returned with
/// <see cref="FactInputBudgetStatus.OverBudget"/> rather than silently exceeding the cap.
/// </para>
/// </remarks>
public static class FactInputBudget
{
    /// <summary>The marker inserted where a source text was cut.</summary>
    private const string TruncationMarker = "\n[…truncated…]\n";

    /// <summary>The estimated token count of a serialised data object: its UTF-8 byte count divided by four, rounded up.</summary>
    /// <param name="utf8Json">The serialised data object.</param>
    /// <returns>The estimate the total-budget loop compares to <c>Facts:MaxInputTokens</c>.</returns>
    public static int EstimateTokens(string utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        return (Encoding.UTF8.GetByteCount(utf8Json) + 3) / 4;
    }

    /// <summary>
    /// Builds the plan of one fact-extraction call: ordered, alias-assigned, per-source-truncated sources
    /// brought under the total budget.
    /// </summary>
    /// <param name="language">The run's language; part of the serialised data object, so it counts towards the estimate.</param>
    /// <param name="candidates">The episode's sources; ordered by <c>created_at</c> ascending, tie-broken by id.</param>
    /// <param name="limits">The <c>Facts</c> bounds in force.</param>
    /// <returns>The plan; its <see cref="FactInputPlan.Status"/> decides whether it may be sent.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No candidate is supplied, every candidate carries no text, or <paramref name="limits"/> is out of range (a programming error; the caller rejects an empty sheet with 409 <c>fact_sheet_sources_insufficient</c> first).</exception>
    public static FactInputPlan Plan(Language language, IReadOnlyList<FactSourceCandidate> candidates, FactInputLimits limits)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(limits);
        GuardLimits(limits);

        if (candidates.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates), candidates, "A fact-extraction call needs at least one source.");
        }

        var excluded = new List<Guid>();
        var kept = new List<FactSourceCandidate>(Math.Min(candidates.Count, limits.MaxSources));
        foreach (var candidate in candidates.OrderBy(item => item.CreatedAt).ThenBy(item => item.SourceId))
        {
            // A url with no extracted_text, or a user note with no note_text, carries nothing: it is dropped
            // before alias assignment and reported, never an error. A user note is never dropped merely for
            // having no extracted_text — it has none by design (README CR-4).
            if (string.IsNullOrWhiteSpace(candidate.Text))
            {
                excluded.Add(candidate.SourceId);
                continue;
            }

            // More than MaxSources sources is a caller-side selection, never alias s11: the excess is
            // reported, not an error.
            if (kept.Count == limits.MaxSources)
            {
                excluded.Add(candidate.SourceId);
                continue;
            }

            kept.Add(candidate);
        }

        if (kept.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates), candidates, "Every source carries no text.");
        }

        var floor = Math.Min(FactInputLimits.MinimumPerSourceCharCap, limits.PerSourceCharCap);
        var count = kept.Count;
        var full = new string[count];
        var texts = new string[count];
        var caps = new int[count];
        for (var i = 0; i < count; i++)
        {
            // NFC, so the cap decision, the cut and the estimate all read the same text; and sanitised, so no
            // unpaired surrogate or C0 control character reaches the object or the estimate.
            full[i] = FactDataObjectJson.Sanitize(kept[i].Text!).Normalize(NormalizationForm.FormC);

            if (kept[i].Kind == FactSourceKind.UserNote)
            {
                caps[i] = int.MaxValue;
                texts[i] = full[i];
                continue;
            }

            caps[i] = limits.PerSourceCharCap;
            texts[i] = Truncate(full[i], caps[i]);
        }

        var json = Serialize(language, kept, texts);
        var tokens = EstimateTokens(json);

        while (tokens > limits.MaxInputTokens)
        {
            var index = LargestTruncatable(kept, full, caps, floor);
            if (index < 0)
            {
                break;
            }

            // Halve the largest truncatable source's effective size; each pass strictly reduces the picked
            // source's budget down to the floor, so the loop terminates.
            var current = Math.Min(caps[index], full[index].Length);
            caps[index] = Math.Max(floor, current / 2);
            texts[index] = Truncate(full[index], caps[index]);

            json = Serialize(language, kept, texts);
            tokens = EstimateTokens(json);
        }

        var sources = BuildInputs(kept, texts);
        var truncated = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(texts[i], full[i], StringComparison.Ordinal))
            {
                truncated.Add(sources[i].Alias);
            }
        }

        return new FactInputPlan(
            sources,
            excluded,
            truncated,
            tokens,
            tokens <= limits.MaxInputTokens ? FactInputBudgetStatus.WithinBudget : FactInputBudgetStatus.OverBudget);
    }

    /// <summary>The index of the truncatable source with the largest effective size, or <c>-1</c> when none can shrink further.</summary>
    private static int LargestTruncatable(List<FactSourceCandidate> kept, string[] full, int[] caps, int floor)
    {
        var best = -1;
        var bestSize = 0;
        for (var i = 0; i < kept.Count; i++)
        {
            if (kept[i].Kind != FactSourceKind.Url)
            {
                continue;
            }

            var size = Math.Min(caps[i], full[i].Length);
            if (size <= floor || size <= bestSize)
            {
                continue;
            }

            best = i;
            bestSize = size;
        }

        return best;
    }

    private static string Serialize(Language language, List<FactSourceCandidate> kept, string[] texts) =>
        FactDataObjectJson.Serialize(language, BuildInputs(kept, texts));

    private static FactSourceInput[] BuildInputs(List<FactSourceCandidate> kept, string[] texts)
    {
        var inputs = new FactSourceInput[kept.Count];
        for (var i = 0; i < kept.Count; i++)
        {
            inputs[i] = new FactSourceInput(
                string.Create(CultureInfo.InvariantCulture, $"s{i + 1}"),
                kept[i].SourceId,
                kept[i].Title,
                texts[i],
                kept[i].Kind == FactSourceKind.UserNote);
        }

        return inputs;
    }

    /// <summary>
    /// Cuts <paramref name="text"/> to <paramref name="cap"/> characters as head 60 % + marker + tail 40 %,
    /// on text-element boundaries so a surrogate pair or a combining sequence is never split
    /// (02.5.3-T1). Deterministic: the same text and cap always yield the same cut.
    /// </summary>
    private static string Truncate(string text, int cap)
    {
        if (text.Length <= cap)
        {
            return text;
        }

        var headTarget = cap * 6 / 10;
        var tailTarget = cap - headTarget;
        var lastTailBoundary = text.Length - tailTarget;
        var span = text.AsSpan();

        var headEnd = text.Length;
        var tailStart = 0;
        var headFound = false;
        var position = 0;
        while (position < span.Length)
        {
            if (!headFound && position >= headTarget)
            {
                headEnd = position;
                headFound = true;
            }

            if (position <= lastTailBoundary)
            {
                tailStart = position;
            }

            position += StringInfo.GetNextTextElementLength(span[position..]);
        }

        if (tailStart < headEnd)
        {
            tailStart = headEnd;
        }

        return string.Concat(text[..headEnd], TruncationMarker, text[tailStart..]);
    }

    private static void GuardLimits(FactInputLimits limits)
    {
        if (limits.MaxSources < 1 || limits.MaxSources > FactInputLimits.MaxSourcesCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), limits.MaxSources, $"MaxSources must be 1..{FactInputLimits.MaxSourcesCeiling}.");
        }

        if (limits.PerSourceCharCap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), limits.PerSourceCharCap, "PerSourceCharCap must be positive.");
        }

        if (limits.MaxInputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), limits.MaxInputTokens, "MaxInputTokens must be positive.");
        }
    }
}
