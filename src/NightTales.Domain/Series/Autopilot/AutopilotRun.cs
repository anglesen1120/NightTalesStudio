using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// One autopilot run of a series (00 overview §6 <c>autopilot_runs</c>). This is the shell 09.2.2-T1
/// owns — the base columns of plan §3; 02.8.3-T1 adds its state-machine columns and the behaviour.
/// </summary>
public sealed class AutopilotRun : AggregateRoot, IUserOwned, IAuditable
{
    private AutopilotRun()
    {
    }

    public Guid UserId { get; private set; }

    public Guid SeriesId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Placeholder FK to <c>episode_ideas</c>, whose constraint 02.8.1 adds.</summary>
    public Guid? IdeaId { get; private set; }

    public Guid? EpisodeId { get; private set; }

    public AutopilotOutcome? Outcome { get; private set; }

    /// <summary>The <c>gate_results</c> JSON document (<c>gates.v1</c>, 02.8.2 owns its shape).</summary>
    public string? GateResults { get; private set; }

    public string? PauseReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
