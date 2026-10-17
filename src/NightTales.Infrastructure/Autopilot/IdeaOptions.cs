using System.Globalization;
using Microsoft.Extensions.Options;
using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Infrastructure.Autopilot;

/// <summary>
/// The <c>Autopilot:Ideas</c> section (02.8.1-T1 plan §3.2/§7.1): how many candidates one run asks for and the
/// per-field limits <see cref="IdeaValidator"/> enforces.
/// </summary>
/// <remarks>
/// <para>
/// Every key repeats the plan's value as its in-code default, so a host without the block still behaves exactly
/// as specified. The per-step model, effort and timeout of the idea call are the <c>OpenAi</c> section's
/// (<c>OpenAi:Models:Ideas</c>, <c>OpenAi:Effort:Ideas</c>, <c>OpenAi:RequestTimeout:Ideas</c>), and its
/// <c>max_output_tokens</c> is read from <c>price_versions.limits.ideas</c> — never a configuration key
/// (02 README T56) — so neither is repeated here.
/// </para>
/// <para>
/// <see cref="Limits"/> is the Domain record itself, so the numbers <c>IdeaValidator</c> enforces and the numbers
/// a host configures are one type. <see cref="Count"/> is clamped to <see cref="MaxCount"/> by the one producer
/// (the context provider); <see cref="IdeaOptionsValidator"/> refuses a section where it could not be.
/// </para>
/// </remarks>
public sealed class IdeaOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Autopilot:Ideas";

    /// <summary>The default of <see cref="Count"/>; the plan's N.</summary>
    public const int DefaultCount = 5;

    /// <summary>The default of <see cref="MaxCount"/>.</summary>
    public const int DefaultMaxCount = 10;

    /// <summary>
    /// The largest <see cref="MaxCount"/> a host may configure: the <c>ideas.v1</c> schema's <c>maxItems</c>, so a
    /// configured count can never ask for more candidates than a schema-valid response may carry.
    /// </summary>
    public const int MaxCountCeiling = 10;

    /// <summary>The number of ideas one run asks the model for (<c>Autopilot:Ideas:Count</c>); 5.</summary>
    public int Count { get; set; } = DefaultCount;

    /// <summary>The most candidates one run may keep (<c>Autopilot:Ideas:MaxCount</c>); 10.</summary>
    public int MaxCount { get; set; } = DefaultMaxCount;

    /// <summary>The per-field limits <see cref="IdeaValidator"/> enforces (<c>Autopilot:Ideas:Limits</c>).</summary>
    public IdeaLimits Limits { get; set; } = new();
}

/// <summary>
/// Fails the start-up on an unusable <c>Autopilot:Ideas</c> section (02.8.1-T1): a count outside the schema's
/// bounds, a count the run would then have to drop, a limit set <see cref="IdeaValidator"/> cannot walk, or a
/// limit <em>looser</em> than the plan's pinned column widths and <c>ideas.v1</c> beat band (QA defect D4:
/// limits may only tighten).
/// </summary>
/// <remarks>
/// Public, like <c>FactsOptionsValidator</c>, because the start-up contract is asserted by a unit test and the
/// ceiling constants are the test's own numbers.
/// </remarks>
public sealed class IdeaOptionsValidator : IValidateOptions<IdeaOptions>
{
    /// <summary>
    /// The plan's numbers, which the storage layer pins (QA defect D4): the <c>episode_ideas</c> columns are
    /// sized from these widths and the <c>ideas.v1</c> schema allows this beat band, so a configured value may
    /// only <em>tighten</em> them — a wider field would insert-fail with a <c>22001</c> <em>after</em> the paid
    /// call, and a wider band could not be honoured by any schema-valid answer.
    /// </summary>
    /// <remarks>
    /// Read from the Domain record's own default constructor, exactly as
    /// <c>EpisodeIdeaConfiguration.ColumnWidths</c> reads the column widths, so the two cannot drift.
    /// </remarks>
    private static readonly IdeaLimits PlanLimits = new();

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, IdeaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var limits = options.Limits;

        if (options.Count < 1 || options.Count > IdeaOptions.MaxCountCeiling)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{IdeaOptions.SectionName}:{nameof(IdeaOptions.Count)}' must be 1..{IdeaOptions.MaxCountCeiling}."));
        }

        if (options.MaxCount < 1 || options.MaxCount > IdeaOptions.MaxCountCeiling)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{IdeaOptions.SectionName}:{nameof(IdeaOptions.MaxCount)}' must be 1..{IdeaOptions.MaxCountCeiling}."));
        }

        if (options.Count > options.MaxCount)
        {
            failures.Add($"'{IdeaOptions.SectionName}:{nameof(IdeaOptions.Count)}' cannot exceed '{IdeaOptions.SectionName}:{nameof(IdeaOptions.MaxCount)}'.");
        }

        if (!limits.IsValid)
        {
            failures.Add($"'{IdeaOptions.SectionName}:Limits' must have positive field widths and a beat band with MaxBeats >= MinBeats.");
        }

        // QA defect D4: limits may only tighten the plan's numbers. The widths are the varchar widths of
        // episode_ideas and the beat band is the schema's minItems/maxItems, so a looser value is either a
        // post-call 22001 or a requirement no valid answer can satisfy.
        AddWidth(failures, nameof(IdeaLimits.TitleChars), limits.TitleChars, PlanLimits.TitleChars);
        AddWidth(failures, nameof(IdeaLimits.LoglineChars), limits.LoglineChars, PlanLimits.LoglineChars);
        AddWidth(failures, nameof(IdeaLimits.HookChars), limits.HookChars, PlanLimits.HookChars);
        AddWidth(failures, nameof(IdeaLimits.SettingChars), limits.SettingChars, PlanLimits.SettingChars);

        if (limits.MinBeats < PlanLimits.MinBeats)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{IdeaOptions.SectionName}:Limits:{nameof(IdeaLimits.MinBeats)}' must be {PlanLimits.MinBeats}..{PlanLimits.MaxBeats} (the ideas.v1 beat band); limits may only tighten."));
        }

        if (limits.MaxBeats > PlanLimits.MaxBeats)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{IdeaOptions.SectionName}:Limits:{nameof(IdeaLimits.MaxBeats)}' must be {PlanLimits.MinBeats}..{PlanLimits.MaxBeats} (the ideas.v1 beat band); limits may only tighten."));
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Adds a failure when a configured character cap is wider than the column it bounds (a value equal to or
    /// below the plan's number succeeds).
    /// </summary>
    private static void AddWidth(List<string> failures, string key, int configured, int plan)
    {
        if (configured > plan)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{IdeaOptions.SectionName}:Limits:{key}' must be 1..{plan} (the episode_ideas column width, pinned to the plan default); limits may only tighten."));
        }
    }
}
