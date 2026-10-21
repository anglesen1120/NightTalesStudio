using System.Globalization;
using Microsoft.Extensions.Options;
using NightTales.Domain.Series.Bible;

namespace NightTales.Infrastructure.Bible;

/// <summary>
/// The <c>Bible</c> configuration section (02.6.3-T1 task Design, plan §3.2/§7.1): the content limits of the
/// bible, the caps of the proposal job and the prompt budget. Bound and validated when the host starts, never
/// lazily on the first request.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Limits"/> is the Domain record itself, so the numbers the validator enforces and the numbers a host
/// configures are one type, and every property's default is the plan's value: a host whose section is missing or
/// partial still enforces the plan.
/// </para>
/// <para>
/// <see cref="Proposal"/> and <see cref="Prompt"/> belong to 02.6.3-T2 (the proposal job): they are declared here
/// because they are the same section, and the Api, the Worker and the test harness all bind it from the one
/// registration.
/// </para>
/// </remarks>
public sealed class BibleOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bible";

    /// <summary>The content limits <c>BibleContentValidator</c> enforces (<c>Bible:Limits</c>).</summary>
    public BibleLimits Limits { get; init; } = new();

    /// <summary>The proposal job's caps (<c>Bible:Proposal</c>, 02.6.3-T2).</summary>
    public BibleProposalOptions Proposal { get; init; } = new();

    /// <summary>The proposal prompt's budget (<c>Bible:Prompt</c>, 02.6.3-T2).</summary>
    public BiblePromptOptions Prompt { get; init; } = new();

    /// <summary>The avoid-plots list the writer receives (<c>Bible:AvoidPlots</c>, 02.6.3-T3).</summary>
    public BibleAvoidPlotsOptions AvoidPlots { get; init; } = new();
}

/// <summary>
/// The caps of the bible proposal job (02.6.3-T2 plan §7.1 step 5): how much of one run's model output survives
/// post-processing, and how much of the approved story the prompt carries.
/// </summary>
public sealed class BibleProposalOptions
{
    /// <summary>Whether the <c>StoryApproved</c> handler enqueues proposal runs at all (the T2 kill-switch).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The most ops one proposal keeps, all kinds together.</summary>
    public int MaxOps { get; init; } = 10;

    /// <summary>The most <c>add_character</c> ops one proposal keeps.</summary>
    public int MaxCharacterOps { get; init; } = 3;

    /// <summary>The most <c>add_place</c> ops one proposal keeps.</summary>
    public int MaxPlaceOps { get; init; } = 3;

    /// <summary>The most <c>add_rule</c> ops one proposal keeps.</summary>
    public int MaxRuleOps { get; init; } = 2;

    /// <summary>The most characters of the approved story body the prompt carries, counted from the end.</summary>
    public int MaxStoryChars { get; init; } = 24000;
}

/// <summary>
/// The bible proposal prompt's budget (02.6.3-T2 plan §7.1 step 3): how many summaries the model sees and the
/// total that the prompt's data object may use. 02.6.3-T3 proves the writer-snapshot budget against the same
/// numbers.
/// </summary>
public sealed class BiblePromptOptions
{
    /// <summary>The newest episode summaries the payload carries.</summary>
    public int MaxSummaries { get; init; } = 50;

    /// <summary>
    /// The most UTF-8 bytes the serialised writer <c>bible</c> object may use (02.6.3-T3). 36,000, not the task's
    /// 24,000: the task's convergence proof counts characters, and Vietnamese text is 1.5 to 3 bytes per character.
    /// </summary>
    public int MaxChars { get; init; } = 36000;

    /// <summary>The most characters of one voice-notes text the writer snapshot keeps (02.6.3-T3 step S1).</summary>
    public int MaxVoiceNotesChars { get; init; } = 500;

    /// <summary>The most characters of one character or place description the writer snapshot keeps (02.6.3-T3 step S2).</summary>
    public int MaxDescriptionChars { get; init; } = 150;

    /// <summary>The newest episode summaries whose full text the writer snapshot keeps; older ones keep only their title (02.6.3-T3 step S3).</summary>
    public int KeepFullSummaryCount { get; init; } = 25;

    /// <summary>The most entries of each array (rules, places, characters) the writer snapshot keeps (02.6.3-T3 steps S4-S6).</summary>
    public int MaxArrayEntries { get; init; } = 15;
}

/// <summary>
/// The writer's avoid-plots list (02.6.3-T3): the first sentence of each recent episode summary, trimmed and
/// deduplicated, so the writer does not repeat a plot the series already used.
/// </summary>
public sealed class BibleAvoidPlotsOptions
{
    /// <summary>The most entries the list carries.</summary>
    public int Max { get; init; } = 25;

    /// <summary>The most characters one entry keeps.</summary>
    public int MaxCharsPerEntry { get; init; } = 120;
}

/// <summary>
/// Validates the <c>Bible</c> section at start-up (02.6.3-T1 task Design; the same shape as
/// <c>SourcesOptionsValidator</c>). Every limit must be positive: a zero limit would make every save a 400, and a
/// negative count cap can never be satisfied. Every limit also has a hard ceiling, because a number a host can
/// raise without bound is a number a request can use to make one row, one prompt or one 400 grow without bound.
/// </summary>
/// <remarks>
/// <para>
/// Public, like <c>SourcesOptionsValidator</c>, because this solution has no <c>InternalsVisibleTo</c> and the
/// start-up contract is asserted by a unit test — the ceiling constants included, so the test and the validator
/// can never disagree about the number a configuration must stay under.
/// </para>
/// <para>
/// <see cref="MaxContentBytesCeiling"/> is the host's own whole-request cap (BD12, 1 MiB): a bible larger than the
/// body that carries it could never be written by the route that owns it.
/// </para>
/// </remarks>
public sealed class BibleOptionsValidator : IValidateOptions<BibleOptions>
{
    /// <summary>The largest <c>Limits:MaxContentBytes</c> a host may configure: the 1 MiB request-body cap.</summary>
    public const int MaxContentBytesCeiling = 1_048_576;

    /// <summary>The largest <c>Limits:MaxCharacters</c>, <c>MaxPlaces</c> and <c>MaxRules</c>.</summary>
    public const int MaxArrayEntries = 1000;

    /// <summary>
    /// The largest value of any <c>*Chars</c> limit a host may configure (the per-field text limits and
    /// <c>Proposal:MaxStoryChars</c>). <c>Prompt:MaxChars</c> has its own, larger budget below.
    /// </summary>
    public const int MaxTextChars = 100_000;

    /// <summary>The largest value of any of the proposal's op caps.</summary>
    public const int MaxProposalOps = 1000;

    /// <summary>The largest <c>Prompt:MaxSummaries</c> a host may configure.</summary>
    public const int MaxPromptSummaries = 10_000;

    /// <summary>The largest <c>Prompt:MaxChars</c> a host may configure: the proposal prompt's whole budget.</summary>
    public const int MaxPromptChars = 1_000_000;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BibleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var limits = options.Limits;
        var proposal = options.Proposal;
        var prompt = options.Prompt;
        var failures = new List<string>();

        RequirePositive(failures, limits.NarratorNameChars, "Limits:NarratorNameChars");
        RequirePositive(failures, limits.NarratorPersonaChars, "Limits:NarratorPersonaChars");
        RequirePositive(failures, limits.VoiceNotesChars, "Limits:VoiceNotesChars");
        RequirePositive(failures, limits.MaxCharacters, "Limits:MaxCharacters");
        RequirePositive(failures, limits.MaxPlaces, "Limits:MaxPlaces");
        RequirePositive(failures, limits.MaxRules, "Limits:MaxRules");
        RequirePositive(failures, limits.NameChars, "Limits:NameChars");
        RequirePositive(failures, limits.DescriptionChars, "Limits:DescriptionChars");
        RequirePositive(failures, limits.RuleChars, "Limits:RuleChars");
        RequirePositive(failures, limits.SummaryTitleChars, "Limits:SummaryTitleChars");
        RequirePositive(failures, limits.SummaryChars, "Limits:SummaryChars");
        RequirePositive(failures, limits.MaxContentBytes, "Limits:MaxContentBytes");
        RequirePositive(failures, proposal.MaxOps, "Proposal:MaxOps");
        RequirePositive(failures, proposal.MaxStoryChars, "Proposal:MaxStoryChars");
        RequirePositive(failures, prompt.MaxSummaries, "Prompt:MaxSummaries");
        RequirePositive(failures, prompt.MaxChars, "Prompt:MaxChars");
        RequirePositive(failures, prompt.MaxVoiceNotesChars, "Prompt:MaxVoiceNotesChars");
        RequirePositive(failures, prompt.MaxDescriptionChars, "Prompt:MaxDescriptionChars");
        RequirePositive(failures, prompt.KeepFullSummaryCount, "Prompt:KeepFullSummaryCount");
        RequirePositive(failures, prompt.MaxArrayEntries, "Prompt:MaxArrayEntries");
        RequirePositive(failures, options.AvoidPlots.Max, "AvoidPlots:Max");
        RequirePositive(failures, options.AvoidPlots.MaxCharsPerEntry, "AvoidPlots:MaxCharsPerEntry");
        RequireNotNegative(failures, proposal.MaxCharacterOps, "Proposal:MaxCharacterOps");
        RequireNotNegative(failures, proposal.MaxPlaceOps, "Proposal:MaxPlaceOps");
        RequireNotNegative(failures, proposal.MaxRuleOps, "Proposal:MaxRuleOps");

        RequireAtMost(failures, limits.NarratorNameChars, "Limits:NarratorNameChars", MaxTextChars);
        RequireAtMost(failures, limits.NarratorPersonaChars, "Limits:NarratorPersonaChars", MaxTextChars);
        RequireAtMost(failures, limits.VoiceNotesChars, "Limits:VoiceNotesChars", MaxTextChars);
        RequireAtMost(failures, limits.NameChars, "Limits:NameChars", MaxTextChars);
        RequireAtMost(failures, limits.DescriptionChars, "Limits:DescriptionChars", MaxTextChars);
        RequireAtMost(failures, limits.RuleChars, "Limits:RuleChars", MaxTextChars);
        RequireAtMost(failures, limits.SummaryTitleChars, "Limits:SummaryTitleChars", MaxTextChars);
        RequireAtMost(failures, limits.SummaryChars, "Limits:SummaryChars", MaxTextChars);
        RequireAtMost(failures, limits.MaxContentBytes, "Limits:MaxContentBytes", MaxContentBytesCeiling);
        RequireAtMost(failures, limits.MaxCharacters, "Limits:MaxCharacters", MaxArrayEntries);
        RequireAtMost(failures, limits.MaxPlaces, "Limits:MaxPlaces", MaxArrayEntries);
        RequireAtMost(failures, limits.MaxRules, "Limits:MaxRules", MaxArrayEntries);
        RequireAtMost(failures, proposal.MaxOps, "Proposal:MaxOps", MaxProposalOps);
        RequireAtMost(failures, proposal.MaxCharacterOps, "Proposal:MaxCharacterOps", MaxProposalOps);
        RequireAtMost(failures, proposal.MaxPlaceOps, "Proposal:MaxPlaceOps", MaxProposalOps);
        RequireAtMost(failures, proposal.MaxRuleOps, "Proposal:MaxRuleOps", MaxProposalOps);
        RequireAtMost(failures, proposal.MaxStoryChars, "Proposal:MaxStoryChars", MaxTextChars);
        RequireAtMost(failures, prompt.MaxSummaries, "Prompt:MaxSummaries", MaxPromptSummaries);
        RequireAtMost(failures, prompt.MaxChars, "Prompt:MaxChars", MaxPromptChars);
        RequireAtMost(failures, prompt.MaxVoiceNotesChars, "Prompt:MaxVoiceNotesChars", MaxTextChars);
        RequireAtMost(failures, prompt.MaxDescriptionChars, "Prompt:MaxDescriptionChars", MaxTextChars);
        RequireAtMost(failures, prompt.KeepFullSummaryCount, "Prompt:KeepFullSummaryCount", MaxPromptSummaries);
        RequireAtMost(failures, prompt.MaxArrayEntries, "Prompt:MaxArrayEntries", MaxArrayEntries);
        RequireAtMost(failures, options.AvoidPlots.Max, "AvoidPlots:Max", MaxPromptSummaries);
        RequireAtMost(failures, options.AvoidPlots.MaxCharsPerEntry, "AvoidPlots:MaxCharsPerEntry", MaxTextChars);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Records a failure when a limit is zero or negative.</summary>
    private static void RequirePositive(List<string> failures, int value, string key)
    {
        if (value <= 0)
        {
            failures.Add($"'{BibleOptions.SectionName}:{key}' must be greater than zero.");
        }
    }

    /// <summary>Records a failure when a cap is negative (zero disables the kind, which is allowed).</summary>
    private static void RequireNotNegative(List<string> failures, int value, string key)
    {
        if (value < 0)
        {
            failures.Add($"'{BibleOptions.SectionName}:{key}' cannot be negative.");
        }
    }

    /// <summary>
    /// Records a failure when a limit is above its hard ceiling, naming the key and the ceiling: a host that
    /// raised it has to be told which number it must come back under (no value of a secret is involved).
    /// </summary>
    private static void RequireAtMost(List<string> failures, int value, string key, int ceiling)
    {
        if (value > ceiling)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{BibleOptions.SectionName}:{key}' must be at most {ceiling}."));
        }
    }
}
