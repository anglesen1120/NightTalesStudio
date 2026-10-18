using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Infrastructure.Autopilot;

/// <summary>
/// Builds the variety guard's <c>structure_plan</c> for one episode at story-generation time (02.8.4-T1 plan
/// §"Steering and verification wiring" [DECISION]): the episode's own <c>used</c> idea plus the series'
/// <em>current</em> approved-episode history.
/// </summary>
/// <remarks>
/// <para>
/// <b>One idea read and one bounded history read.</b> The first query is the only autopilot marker of an episode
/// — the <c>episode_ideas</c> row whose <c>episode_id</c> is the episode and whose status is <c>used</c> — and it
/// projects the series id, the idea's stored structure tags and its beats, never a title, logline or hook. No
/// such row means a user-created episode, so the provider answers <see langword="null"/> and nothing steers it.
/// The tenant filter of the read model hides another owner's row, so a foreign id answers exactly like an unknown
/// one (D10), and the episode id is the only input.
/// </para>
/// <para>
/// The history is read once, newest first, capped at the wider of the guard's two windows
/// (<c>max(WindowK, TemplateWindowK)</c>, never more than the idea context's own history cap,
/// <see cref="IdeaContextProvider.HistoryCount"/>), through the same <see cref="IEpisodeStructureHistory"/> the
/// idea step fills from — the plan is rebuilt from the live window rather than reused from the idea, so it stays
/// fresh when the series approves an episode between the idea and the story.
/// </para>
/// <para>
/// The result is steering only: the provider never rejects (the hard blocks belong to the idea step) and it still
/// steers when <see cref="VarietyOptions.Enabled"/> is <see langword="false"/>, because the flag disables the idea
/// step's rejection and penalty alone. Nothing is logged and no story text is ever read (D19).
/// </para>
/// </remarks>
/// <param name="readDb">The tenant-filtered read model of the caller's scope.</param>
/// <param name="history">The series' approved-episode structure history, the same reader the idea step uses.</param>
/// <param name="options">The <c>Autopilot:Variety</c> section: the two windows the assessment uses.</param>
public sealed class StructurePlanProvider(
    IReadDb readDb,
    IEpisodeStructureHistory history,
    VarietyOptions options) : IStructurePlanProvider
{
    /// <inheritdoc />
    public async ValueTask<StructurePlan?> GetForEpisodeAsync(Guid episodeId, CancellationToken ct)
    {
        // The `used` idea is the only autopilot marker: no row means a user-created episode, which is never
        // steered (task [DECISION]). At most one row exists (the partial unique index on `episode_id`).
        var idea = await readDb.Query<EpisodeIdea>()
            .Where(row => row.EpisodeId == episodeId && row.Status == IdeaStatus.Used)
            .Select(row => new { row.SeriesId, row.Structure })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (idea is null)
        {
            return null;
        }

        // One bounded read, newest first: the guard's window is the wider of the two window sizes, clamped to the
        // idea context's own history cap so a hand-built (unvalidated) options instance cannot ask the reader for
        // more rows than the idea step ever assessed, nor an unbounded read.
        var window = Math.Min(Math.Max(options.WindowK, options.TemplateWindowK), IdeaContextProvider.HistoryCount);
        var recent = await history.GetRecentAsync(idea.SeriesId, window, ct).ConfigureAwait(false);

        var batch = VarietyGuard.AssessBatch(recent, options);

        // The candidate is the idea's own structure: only its tags and beats are read (never a title, a logline or
        // a hook), so the guard sees the same shape it saw at idea time (D19).
        var candidate = new IdeaCandidate(
            Title: string.Empty,
            Logline: string.Empty,
            Hook: string.Empty,
            Setting: string.Empty,
            Structure: idea.Structure.Tags,
            Beats: idea.Structure.Beats);

        var decision = VarietyGuard.Evaluate(candidate, batch, options);
        var plan = VarietyGuard.ToPlan(batch, decision);

        // [DECISION] A dimension the guard has no suggestion for keeps the idea's own value as the required one,
        // as long as the value can steer: `other:<text>` is not a stable vocabulary id, and an empty value steers
        // nothing. The guard's own suggestion always wins when it has one, and a value the plan already forbids is
        // never required — otherwise the writer would be told to use and avoid the same value, and the 02.8.2
        // gate would then fail on the forbidden half (R4 already refuses to require a forbidden value).
        var merged = plan with
        {
            Required = new StructureRequirements(
                Required(plan.Required.SubGenre, idea.Structure.Tags.SubGenre, plan.Forbidden.SubGenre),
                Required(plan.Required.Setting, idea.Structure.Tags.Setting, plan.Forbidden.Setting),
                Required(plan.Required.Opening, idea.Structure.Tags.Opening, plan.Forbidden.Opening),
                Required(plan.Required.TwistType, idea.Structure.Tags.TwistType, plan.Forbidden.TwistType)),
        };

        // Nothing to steer: the writer data object omits `structure_plan` (plan §"Steering and verification").
        return merged.IsEmpty ? null : merged;
    }

    /// <summary>
    /// The guard's suggestion when it has one, otherwise the idea's own value when that value can steer and the
    /// plan does not already forbid it.
    /// </summary>
    private static string? Required(string? suggested, string? own, IReadOnlyList<string> forbidden) =>
        string.IsNullOrWhiteSpace(suggested) && TryOwn(own, out string trimmed) && !IsForbidden(trimmed, forbidden)
            ? trimmed
            : suggested;

    /// <summary>
    /// Whether an idea's own tag value may become a requirement: non-blank and not an <c>other:</c> catch-all
    /// (an unmapped value is not a stable vocabulary id).
    /// </summary>
    private static bool TryOwn(string? value, out string trimmed)
    {
        trimmed = value?.Trim() ?? string.Empty;

        return trimmed.Length > 0 && !trimmed.StartsWith("other:", StringComparison.Ordinal);
    }

    /// <summary>Whether the plan already forbids <paramref name="value"/> (the plan's values are trimmed).</summary>
    private static bool IsForbidden(string value, IReadOnlyList<string> forbidden)
    {
        foreach (string entry in forbidden)
        {
            if (string.Equals(entry.Trim(), value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
