namespace NightTales.Application.Abstractions.Usage;

/// <summary>The reserve bound of one run (02 README D1/D14).</summary>
/// <param name="WorstCaseUsd">The summed worst-case cost of every paid call.</param>
/// <param name="MaxInputTokens">The P6 input bound: UTF-8 bytes of the assembled prompt, never fewer than its tokens.</param>
/// <param name="MaxOutputTokens">The writer's <c>max_output_tokens</c>, the largest single step.</param>
public sealed record UsageBound(decimal WorstCaseUsd, int MaxInputTokens, int MaxOutputTokens);
