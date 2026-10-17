namespace NightTales.Application.Features.Research.SourceFinder;

/// <summary>
/// The inputs <see cref="FindSourcesCostEstimator"/> reads from configuration (02.7.2-T2): the finder step's model
/// (<c>OpenAi:Models:Finder</c>, default <c>gpt-6-luna</c>), the tool-call cap of one search and the byte ceiling of
/// its prompt plus data object.
/// </summary>
/// <remarks>
/// The step's <c>max_output_tokens</c> is deliberately not here: README T56/T12 rule that it is
/// <c>price_versions.limits.finder</c> (2,000), so <see cref="FindSourcesCostEstimator.FinderMaxOutputTokens"/> is
/// that constant until the limits block ships with 02.7.2-T3.
/// </remarks>
public sealed class FindSourcesCostOptions
{
    /// <summary>The configuration section name; the model is overridden from <c>OpenAi:Models:Finder</c>.</summary>
    public const string SectionName = "SourceFinder";

    /// <summary>The plan §7 tool-call cap of one search: the number the bound multiplies the per-call fee by.</summary>
    public const int DefaultMaxSearchCalls = 3;

    /// <summary>
    /// The largest <see cref="MaxSearchCalls"/> the validator accepts: a typo guard, not a product limit. The plan
    /// fixes the cap at 3; ten already reserves ten times the fee for a run the prompt tells the model to cap at 3.
    /// </summary>
    public const int MaxMaxSearchCalls = 10;

    /// <summary>The default byte ceiling of the finder prompt and its data object (02.7.2-T2 R4).</summary>
    public const int DefaultMaxInputBytes = 6_000;

    /// <summary>
    /// The largest <see cref="MaxInputBytes"/> the validator accepts: a typo guard. The bound charges the whole
    /// ceiling at the input and the cache-write rate, so an unbounded value would reserve far above any real call.
    /// </summary>
    public const int MaxMaxInputBytes = 100_000;

    /// <summary>The model whose rates price one finder call.</summary>
    public string Model { get; set; } = FindSourcesCostEstimator.DefaultModel;

    /// <summary>The tool-call cap the prompt states and the estimator reserves (plan §7).</summary>
    public int MaxSearchCalls { get; set; } = DefaultMaxSearchCalls;

    /// <summary>The estimated-byte ceiling of the finder prompt and data object (T3 must stay under it).</summary>
    public int MaxInputBytes { get; set; } = DefaultMaxInputBytes;
}
