namespace NightTales.Application.Features.Stories.Commands.GenerateStory;

/// <summary>The 202 body of <c>POST /api/v1/episodes/{id}/story/generate</c>; stored and replayed by the idempotency behaviour.</summary>
/// <param name="GenerationRunId">The queued <c>generation_runs</c> row.</param>
/// <param name="Status">The run status, <c>queued</c>.</param>
/// <param name="Stage">The run stage, <c>queued</c>.</param>
public sealed record GenerateStoryAccepted(Guid GenerationRunId, string Status, string Stage);
