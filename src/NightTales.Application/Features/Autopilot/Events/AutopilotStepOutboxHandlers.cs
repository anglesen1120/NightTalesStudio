using Microsoft.Extensions.Logging;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Episodes.Events;
using NightTales.Domain.Publishing;
using NightTales.Domain.Rendering;
using NightTales.Domain.Research.Events;
using NightTales.Domain.Stories.Events;
using NightTales.Domain.Stories.Generation.Events;

namespace NightTales.Application.Features.Autopilot.Events;

/// <summary>
/// <c>story_generated.v1</c> → advance the run of the episode (a <c>story_generating</c> run moves to
/// <c>story_gate</c>; the gate itself is the next tick's step).
/// </summary>
public sealed class StoryGeneratedHandler(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<StoryGeneratedHandler> logger) : AutopilotStepOutboxHandler<StoryGenerated>(locator, orchestrator, logger)
{
    /// <inheritdoc />
    protected override Guid EpisodeIdOf(StoryGenerated domainEvent) => domainEvent.EpisodeId;
}

/// <summary>
/// <c>story_generation_failed.v1</c> → advance the run: the regenerate, or the end of a run whose attempt is spent
/// (plan §3.5 rows <c>story_generating → story_generating</c> and <c>→ failed</c>).
/// </summary>
public sealed class StoryGenerationFailedHandler(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<StoryGenerationFailedHandler> logger)
    : AutopilotStepOutboxHandler<StoryGenerationFailed>(locator, orchestrator, logger)
{
    /// <inheritdoc />
    protected override Guid EpisodeIdOf(StoryGenerationFailed domainEvent) => domainEvent.EpisodeId;
}

/// <summary>
/// <c>story_approved.v1</c> → advance the run: the owner's approval of a held story opens
/// <c>waiting_owner → rendering</c>, while the autopilot's own approval only confirms a gate that already moved the
/// run (the compare-and-set makes the second case a no-op).
/// </summary>
public sealed class StoryApprovedHandler(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<StoryApprovedHandler> logger) : AutopilotStepOutboxHandler<StoryApproved>(locator, orchestrator, logger)
{
    /// <inheritdoc />
    protected override Guid EpisodeIdOf(StoryApproved domainEvent) => domainEvent.EpisodeId;
}

/// <summary>
/// <c>fact_sheet_generated.v1</c> → advance the run: a <c>researching</c> run moves to <c>waiting_owner</c> with
/// <c>waiting_for = fact_sheet</c> (plan §3.5 row 205).
/// </summary>
public sealed class FactSheetGeneratedHandler(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<FactSheetGeneratedHandler> logger)
    : AutopilotStepOutboxHandler<FactSheetGenerated>(locator, orchestrator, logger)
{
    /// <inheritdoc />
    protected override Guid EpisodeIdOf(FactSheetGenerated domainEvent) => domainEvent.EpisodeId;
}

/// <summary>
/// <c>fact_sheet_approved.v1</c> → advance the run: the owner's approval opens
/// <c>waiting_owner → story_generating</c> (plan §3.5 row 206). True crime never auto-approves a sheet, so this
/// event is always the owner's.
/// </summary>
public sealed class FactSheetApprovedHandler(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<FactSheetApprovedHandler> logger)
    : AutopilotStepOutboxHandler<FactSheetApproved>(locator, orchestrator, logger)
{
    /// <inheritdoc />
    protected override Guid EpisodeIdOf(FactSheetApproved domainEvent) => domainEvent.EpisodeId;
}

/// <summary>
/// <c>render_succeeded.v1</c> → advance the run: <c>rendering</c> moves to <c>video_gate</c> (plan §3.5 row
/// <c>rendering → video_gate</c>). The payload carries no user id; the locator supplies the run's owner.
/// </summary>
public sealed class RenderSucceededHandler(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<RenderSucceededHandler> logger) : AutopilotStepOutboxHandler<RenderSucceeded>(locator, orchestrator, logger)
{
    /// <inheritdoc />
    protected override Guid EpisodeIdOf(RenderSucceeded domainEvent) => domainEvent.EpisodeId;
}

/// <summary>
/// <c>render_failed.v1</c> → advance the run: the episode is back in <c>story_approved</c>, and a run with no
/// render left ends <c>failed</c> (<c>render_failed</c>; plan §3.5 row <c>rendering → failed</c>). A non-final
/// attempt is left to the retry policy and advances nothing.
/// </summary>
public sealed class RenderFailedHandler(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<RenderFailedHandler> logger) : AutopilotStepOutboxHandler<RenderFailed>(locator, orchestrator, logger)
{
    /// <inheritdoc />
    protected override Guid EpisodeIdOf(RenderFailed domainEvent) => domainEvent.EpisodeId;
}

/// <summary>
/// <c>video_approved.v1</c> → advance the run: the owner's approval of a held video opens
/// <c>waiting_owner → publication_created</c> once the timing guard allows it (plan §3.5 row 222).
/// </summary>
public sealed class VideoApprovedHandler(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<VideoApprovedHandler> logger) : AutopilotStepOutboxHandler<VideoApproved>(locator, orchestrator, logger)
{
    /// <inheritdoc />
    protected override Guid EpisodeIdOf(VideoApproved domainEvent) => domainEvent.EpisodeId;
}

/// <summary>
/// <c>publication_updated.v1</c> → advance the run: the veto notice, the consent, the live result and a cancellation
/// all travel on this event (plan §3.5 rows from <c>publication_created</c> onward).
/// </summary>
public sealed class PublicationUpdatedHandler(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<PublicationUpdatedHandler> logger)
    : AutopilotStepOutboxHandler<PublicationUpdated>(locator, orchestrator, logger)
{
    /// <inheritdoc />
    protected override Guid EpisodeIdOf(PublicationUpdated domainEvent) => domainEvent.EpisodeId;
}
