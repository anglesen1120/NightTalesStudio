using NightTales.Application.Features.Research.Facts;

namespace NightTales.Application.Features.Research.Jobs;

/// <summary>
/// The per-run time budget of <c>GenerateFactSheetJob</c> (02.5.3-T2, 02 README D7), bound from
/// <c>FactSheet:Job</c>. The key has an in-code default, so it is not a required start-up key; a value outside
/// the bounds fails the Worker's start-up.
/// </summary>
/// <remarks>
/// The deadline covers everything one run does around the paid call: the claim and the plan, the free D12
/// source pre-check (<c>OpenAi:Moderation:TimeoutSeconds</c>, 15 s, likewise one timeout per attempt plus its
/// retry wait), and the extraction itself, which the adapter bounds at <c>OpenAi:RequestTimeout:Facts</c> (60 s)
/// per attempt with one retry. A deadline shorter than that cannot cover even one call, so a run would be
/// cancelled mid-flight with the request already sent and billed, and the invalid-output retry could never fit:
/// a shorter deadline can only lose money. The floor is therefore moderation plus two adapter timeouts
/// (<see cref="MinTimeoutSeconds"/>) and the default adds the plan, the save and shutdown headroom.
/// </remarks>
public sealed class FactSheetJobOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "FactSheet:Job";

    /// <summary>
    /// The smallest accepted timeout: the free moderation call (15 s) plus the two adapter timeouts one run can
    /// need (the extraction and its single invalid-output retry, 60 s each) and the headroom of one retry wait.
    /// Below it the paid call is cancelled mid-flight, with the request already sent and billed, so the run
    /// deadline can only lose money.
    /// </summary>
    public const int MinTimeoutSeconds = 150;

    /// <summary>
    /// The largest accepted timeout: the story sweeper's own window (the <c>Story:Job:TimeoutSeconds</c> of 360 s
    /// plus its grace — the point at which that sweeper fails a <c>running</c> run). A longer fact deadline would
    /// let the sweeper fail a healthy run this job still believes is inside its own budget, so the ceiling is
    /// that window, not an arbitrary operational value.
    /// </summary>
    public const int MaxTimeoutSeconds = 360;

    /// <summary>The run deadline, counted from <c>started_at</c>.</summary>
    public int TimeoutSeconds { get; set; } = 240;

    /// <summary>The deadline as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>Checks the timeout; the Worker's options validation calls it.</summary>
    /// <param name="options">The bound options.</param>
    /// <returns>Every problem found; empty when the options are valid.</returns>
    public static IReadOnlyList<string> Validate(FactSheetJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (options.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            problems.Add($"'{SectionName}:{nameof(TimeoutSeconds)}' must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}.");
        }

        return problems;
    }
}

/// <summary>
/// The <c>Facts</c> section as the fact-sheet job reads it (02.5.3-T2): the same input budget and fact ceiling
/// the OpenAI extractor enforces, so the plan the job builds is the one the adapter accepts.
/// </summary>
/// <remarks>
/// The section is bound a second time here on purpose. <c>FactInputBudget.Plan</c> needs the limits before the
/// call, and the layer that owns the budget (Application) cannot reference the Infrastructure options class
/// that binds the section for the adapter (<c>FactsOptions</c>). Both are validated on start, they read the same
/// keys, and <c>OpenAiFactExtractor</c> stays the final guard: a request whose sources exceed these limits is
/// rejected by the adapter, never sent.
/// </remarks>
public sealed class FactSheetExtractionOptions
{
    /// <summary>The configuration section name; the same key <c>FactsOptions</c> binds for the adapter.</summary>
    public const string SectionName = "Facts";

    /// <summary>The smallest <c>MaxFacts</c> the validator accepts.</summary>
    public const int MinMaxFacts = 5;

    /// <summary>The largest <c>MaxFacts</c> the validator accepts; also the <c>facts.v1</c> <c>maxItems</c>.</summary>
    public const int MaxMaxFacts = 40;

    /// <summary>The default of <see cref="MaxFacts"/>: the 02.5.3-T1 fact ceiling.</summary>
    public const int DefaultMaxFacts = MaxMaxFacts;

    /// <summary>How many sources one extraction may send; at most <see cref="FactInputLimits.MaxSourcesCeiling"/> (<c>s1…s10</c>).</summary>
    public int MaxSources { get; set; } = FactInputLimits.MaxSourcesCeiling;

    /// <summary>The per-source character cap before the total budget is applied.</summary>
    public int PerSourceCharCap { get; set; } = FactInputLimits.DefaultPerSourceCharCap;

    /// <summary>The estimated-token ceiling of the serialised data object.</summary>
    public int MaxInputTokens { get; set; } = FactInputLimits.DefaultMaxInputTokens;

    /// <summary>
    /// The fact ceiling <c>FactSheetValidator</c> enforces and the schema's <c>maxItems</c> mirrors; survivors
    /// beyond it are dropped as <c>over_cap</c>.
    /// </summary>
    public int MaxFacts { get; set; } = DefaultMaxFacts;

    /// <summary>The input bounds this section states, as the input budget reads them.</summary>
    /// <returns>The <see cref="FactInputLimits"/> of <see cref="MaxSources"/>, <see cref="PerSourceCharCap"/> and <see cref="MaxInputTokens"/>.</returns>
    public FactInputLimits ToLimits() => new(MaxSources, PerSourceCharCap, MaxInputTokens);

    /// <summary>Checks every key; the composition root's options validation calls it.</summary>
    /// <param name="options">The bound options.</param>
    /// <returns>Every problem found; empty when the options are valid.</returns>
    public static IReadOnlyList<string> Validate(FactSheetExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (options.MaxSources < 1 || options.MaxSources > FactInputLimits.MaxSourcesCeiling)
        {
            problems.Add($"'{SectionName}:{nameof(MaxSources)}' must be 1..{FactInputLimits.MaxSourcesCeiling} (aliases s1..s10).");
        }

        if (options.PerSourceCharCap < FactInputLimits.MinimumPerSourceCharCap)
        {
            problems.Add($"'{SectionName}:{nameof(PerSourceCharCap)}' must be at least {FactInputLimits.MinimumPerSourceCharCap}.");
        }

        if (options.MaxInputTokens < 1 || options.MaxInputTokens > FactInputLimits.MaxMaxInputTokens)
        {
            problems.Add($"'{SectionName}:{nameof(MaxInputTokens)}' must be 1..{FactInputLimits.MaxMaxInputTokens}.");
        }

        if (options.MaxFacts < MinMaxFacts || options.MaxFacts > MaxMaxFacts)
        {
            problems.Add($"'{SectionName}:{nameof(MaxFacts)}' must be {MinMaxFacts}..{MaxMaxFacts}.");
        }

        return problems;
    }
}
