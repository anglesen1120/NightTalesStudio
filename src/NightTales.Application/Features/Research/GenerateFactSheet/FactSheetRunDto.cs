namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// The 202 body of <c>POST /api/v1/episodes/{id}/fact-sheet/generate</c> (02.5.3 plan §4.1); stored and
/// replayed by the idempotency behaviour, so a repeated key answers the same run.
/// </summary>
/// <param name="RunId">The queued <c>generation_runs</c> row.</param>
/// <param name="FactSheetId">The fact sheet the run produced; <c>null</c> while the run has not saved a draft.</param>
/// <param name="Status">The run status, <c>queued</c> at creation.</param>
public sealed record FactSheetRunDto(Guid RunId, Guid? FactSheetId, string Status);
