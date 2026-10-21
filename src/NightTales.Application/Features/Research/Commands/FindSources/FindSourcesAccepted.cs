namespace NightTales.Application.Features.Research.Commands.FindSources;

/// <summary>The 202 body of <c>POST /api/v1/episodes/{id}/sources/find</c>; stored and replayed by the idempotency behaviour.</summary>
/// <param name="GenerationRunId">The queued <c>generation_runs</c> row.</param>
public sealed record FindSourcesAccepted(Guid GenerationRunId);
