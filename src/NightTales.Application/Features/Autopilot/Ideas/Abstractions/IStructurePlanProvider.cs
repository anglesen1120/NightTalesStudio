using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Autopilot.Ideas.Abstractions;

/// <summary>
/// Builds the variety guard's <c>structure_plan</c> for one episode at story-generation time (02.8.4-T1 plan
/// §"Steering and verification wiring" [DECISION]; 02.2.3-T2 declares the <c>structure_plan</c> data-object key).
/// </summary>
/// <remarks>
/// <para>
/// The plan is derived from the series' <em>current</em> history, never from the idea row: the idea may be hours
/// old and the window of recent approved episodes may have grown since, so a stored plan would be stale. The
/// implementation reads the episode's own <c>used</c> idea for the series id, its stored structure tags and its
/// beats — never a story text, a logline or a title (D19) — then calls <c>VarietyGuard.ToPlan</c> over the
/// history the guard read at idea time.
/// </para>
/// <para>
/// It is autopilot-only steering: an episode with no used idea (a user-created one, the only autopilot marker)
/// has no plan, so the writer data object omits <c>structure_plan</c>. The provider never rejects a candidate —
/// the hard blocks belong to the idea step — and it still steers when <c>Autopilot:Variety:Enabled=false</c>,
/// because the flag disables the idea step's rejection and penalty alone.
/// </para>
/// </remarks>
public interface IStructurePlanProvider
{
    /// <summary>Builds the structure plan of one episode from the series' current history.</summary>
    /// <param name="episodeId">The episode to steer; the only input, already scoped to the caller's tenant.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>
    /// The plan the writer's data object carries, or <see langword="null"/> when the episode has no used idea or
    /// the current history yields nothing to steer (then <c>structure_plan</c> is omitted).
    /// </returns>
    ValueTask<StructurePlan?> GetForEpisodeAsync(Guid episodeId, CancellationToken ct);
}
