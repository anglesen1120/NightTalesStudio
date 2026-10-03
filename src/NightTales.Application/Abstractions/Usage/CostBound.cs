namespace NightTales.Application.Abstractions.Usage;

/// <summary>The worst-case USD cost of one paid unit of work, as the budget guard checks it before dispatch (02 README D1).</summary>
/// <param name="WorstCaseUsd">The highest cost the work can reach.</param>
public sealed record CostBound(decimal WorstCaseUsd);
