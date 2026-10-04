using System.Text.Json;

namespace NightTales.Application.Abstractions.Moderation;

/// <summary>
/// Where in a run a moderation check happens (02 README D12). The three D12 stages plus the TL-ruling
/// series-level kinds, so a refusal of a series-level run records <c>stage = &lt;kind&gt;</c> (D8).
/// <see cref="AutopilotGate"/> stays owned by 02.8.2.
/// </summary>
public enum ModerationStage
{
    /// <summary>The episode brief and title, before any paid call.</summary>
    Brief,

    /// <summary>The final draft: title, body, description and tags.</summary>
    Draft,

    /// <summary>The autopilot gate (02.8.2).</summary>
    AutopilotGate,

    /// <summary>A series bible proposal run (02.6.3).</summary>
    BibleProposal,

    /// <summary>An ideas run (02.8.1).</summary>
    Ideas,

    /// <summary>A find-sources run (02.7).</summary>
    FindSources,
}

/// <summary>The stored name of a <see cref="ModerationStage"/>.</summary>
public static class ModerationStageNames
{
    /// <summary>The snake_case name written to <c>moderation_events.stage</c>, for example <c>bible_proposal</c>.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>The stored name.</returns>
    public static string ToWireName(this ModerationStage stage) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(stage.ToString());
}
