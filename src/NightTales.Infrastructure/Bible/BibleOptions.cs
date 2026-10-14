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

    /// <summary>The most characters the bible part of a prompt may use.</summary>
    public int MaxChars { get; init; } = 24000;
}

/// <summary>
/// Validates the <c>Bible</c> section at start-up (02.6.3-T1 task Design; the same shape as
/// <c>SourcesOptionsValidator</c>). Every limit must be positive: a zero limit would make every save a 400, and a
/// negative count cap can never be satisfied.
/// </summary>
/// <remarks>
/// Public, like <c>SourcesOptionsValidator</c>, because this solution has no <c>InternalsVisibleTo</c> and the
/// start-up contract is asserted by a unit test.
/// </remarks>
public sealed class BibleOptionsValidator : IValidateOptions<BibleOptions>
{
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
        RequireNotNegative(failures, proposal.MaxCharacterOps, "Proposal:MaxCharacterOps");
        RequireNotNegative(failures, proposal.MaxPlaceOps, "Proposal:MaxPlaceOps");
        RequireNotNegative(failures, proposal.MaxRuleOps, "Proposal:MaxRuleOps");

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
}
