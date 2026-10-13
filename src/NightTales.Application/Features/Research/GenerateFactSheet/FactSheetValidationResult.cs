using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// The outcome of validating one <c>facts.v1</c> output (02.5.3-T2): the facts to store, the next free fact
/// id, every fact that was dropped and why, and how many facts the model returned. Zero surviving facts is a
/// valid result — the job still saves an empty draft (F13).
/// </summary>
/// <param name="Facts">The surviving facts, in output order, with backend-assigned ids.</param>
/// <param name="NewMaxFactId">The highest id assigned by this run; persisted as <c>max_fact_id</c> and never reused.</param>
/// <param name="Drops">Every dropped fact with its reason and output position.</param>
/// <param name="ModelFactCount">How many facts the model returned, before any drop — recorded on the run.</param>
public sealed record FactSheetValidationResult(
    IReadOnlyList<FactItem> Facts,
    int NewMaxFactId,
    IReadOnlyList<FactDrop> Drops,
    int ModelFactCount);
