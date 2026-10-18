using System.Diagnostics.Metrics;
using System.Text.Json;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The idea steps' instruments (02.8.1-T1 plan §7.1 step 7, §11 monitoring): the one counter of what happened to
/// each candidate a model batch carried. Tags carry bounded values only — the candidate's outcome, never an id,
/// a title or any text (D19).
/// </summary>
/// <remarks>
/// <para>
/// The meter is the autopilot feature's (<c>NightTales.Autopilot</c>), shared with the generator's own instrument
/// <c>idea_generation_duration_seconds</c> — the split <c>SeriesBibleMetrics</c> (Application) and
/// <c>OpenAiBibleProposalMetrics</c> (Infrastructure) already use, so one dashboard selects the meter and finds
/// both.
/// </para>
/// <para>
/// Registered as a singleton: one instrument per process, so a scrape never sees two series for one counter
/// (the <c>StorageMetrics</c> rule).
/// </para>
/// </remarks>
public sealed class IdeaMetrics : IDisposable
{
    /// <summary>The meter the idea steps publish on.</summary>
    public const string MeterName = "NightTales.Autopilot";

    /// <summary>One candidate of a model batch, by outcome.</summary>
    public const string CandidatesTotalName = "idea_candidates_total";

    /// <summary>The tag naming the candidate's outcome: a status wire value or a rejection reason.</summary>
    public const string OutcomeTagName = "outcome";

    /// <summary>The outcome of the candidate the run answered as its pick.</summary>
    public static readonly string PickedOutcome = JsonNamingPolicy.SnakeCaseLower.ConvertName(nameof(IdeaStatus.Picked));

    /// <summary>The distribution of the lexical shingle scores of one batch's candidates (02.8.1-T2 plan §7).</summary>
    public const string NoveltyLexicalScoreName = "idea_novelty_lexical_score";

    /// <summary>The distribution of the novelty judge's scores (02.8.1-T2 plan §7); the calibration histogram.</summary>
    public const string NoveltyJudgeScoreName = "idea_novelty_judge_score";

    /// <summary>A candidate the content policy stopped (02.8.1-T2, D12).</summary>
    public const string ModerationFlagsTotalName = "moderation_flags_total";

    /// <summary>A moderation check that could not complete (02.8.1-T2, D12): the fail-closed event.</summary>
    public const string ModerationUnavailableTotalName = "moderation_unavailable_total";

    /// <summary>The tag naming the moderation stage, alongside the flag counter.</summary>
    public const string StageTagName = "stage";

    /// <summary>The <c>brief</c> stage 02.8.1-T2's per-candidate pre-check records.</summary>
    public static readonly string BriefStage = ModerationStageNames.ToWireName(ModerationStage.Brief);

    /// <summary>A candidate the variety guard blocked, by rule (02.8.4-T1).</summary>
    public const string VarietyRejectionsTotalName = "variety_rejections_total";

    /// <summary>A candidate the variety guard penalised without blocking (02.8.4-T1).</summary>
    public const string VarietyPenaltiesTotalName = "variety_penalty_total";

    /// <summary>The tag naming the guard rule that blocked a candidate, alongside its counter (02.8.4-T1).</summary>
    public const string RuleTagName = "rule";

    /// <summary>
    /// The closed vocabulary of the <see cref="RuleTagName"/> tag: the two rules that block, and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>variety_tag_cap</c> is deliberately absent: the tag-share cap is a penalty, never a rejection
    /// (<see cref="IdeaRejectionReason.VarietyTagCap"/>), so it can never be the rule a candidate was blocked
    /// by and never reaches this tag. A caller that names anything outside this set is refused instead of
    /// publishing a free-text value on a metric (D19).
    /// </remarks>
    public static readonly IReadOnlySet<string> VarietyRuleNames = new HashSet<string>(StringComparer.Ordinal)
    {
        IdeaRejectionReason.VarietyTripleRepeat,
        IdeaRejectionReason.VarietyTemplateRepeat,
    };

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _candidates;
    private readonly Histogram<double> _noveltyLexical;
    private readonly Histogram<double> _noveltyJudge;
    private readonly Counter<long> _moderationFlags;
    private readonly Counter<long> _moderationUnavailable;
    private readonly Counter<long> _varietyRejections;
    private readonly Counter<long> _varietyPenalties;

    /// <summary>Creates the instruments.</summary>
    public IdeaMetrics()
    {
        _candidates = _meter.CreateCounter<long>(
            CandidatesTotalName,
            "1",
            "A candidate of one idea batch, tagged with its outcome: the status it was stored with, or the reason it was dropped.");
        _noveltyLexical = _meter.CreateHistogram<double>(
            NoveltyLexicalScoreName,
            "1",
            "The lexical shingle score of one candidate against the series' corpus, 0…1.");
        _noveltyJudge = _meter.CreateHistogram<double>(
            NoveltyJudgeScoreName,
            "1",
            "The novelty judge's score for one candidate, 0…1; the threshold's calibration reads this.");
        _moderationFlags = _meter.CreateCounter<long>(
            ModerationFlagsTotalName,
            "1",
            "A candidate the content policy stopped, by stage.");
        _moderationUnavailable = _meter.CreateCounter<long>(
            ModerationUnavailableTotalName,
            "1",
            "A moderation check that could not complete; the fail-closed event of the policy pre-check.");
        _varietyRejections = _meter.CreateCounter<long>(
            VarietyRejectionsTotalName,
            "1",
            "A candidate the variety guard blocked, tagged with the rule that blocked it.");
        _varietyPenalties = _meter.CreateCounter<long>(
            VarietyPenaltiesTotalName,
            "1",
            "A candidate the variety guard penalised without blocking; the soft cap's share pressure.");
    }

    /// <summary>
    /// Counts the candidates of one outcome in one add, so a batch that drops five candidates for the same reason
    /// is one instrument update rather than five.
    /// </summary>
    /// <param name="outcome">
    /// The closed outcome vocabulary, one value per candidate: <see cref="PickedOutcome"/> for the run's pick, or
    /// the <see cref="IdeaRejectionReason"/> value the candidate was rejected with (<c>invalid</c> also covers the
    /// candidates beyond <c>MaxCount</c> the failure table drops before validation). The value is the candidate's
    /// most specific outcome, so a rejection is counted once, under its reason.
    /// </param>
    /// <param name="count">How many candidates the outcome covers; must be positive.</param>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    public void Candidates(string outcome, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        _candidates.Add(count, new KeyValuePair<string, object?>(OutcomeTagName, outcome));
    }

    /// <summary>Records one candidate's lexical score, the histogram the thresholds are calibrated on.</summary>
    /// <param name="score">The score, 0…1; a number only, never a tag (D19).</param>
    public void NoveltyLexical(decimal score) => _noveltyLexical.Record((double)score);

    /// <summary>Records one candidate's judge score, the histogram the judge threshold is calibrated on.</summary>
    /// <param name="score">The score, 0…1; a number only, never a tag (D19).</param>
    public void NoveltyJudge(decimal score) => _noveltyJudge.Record((double)score);

    /// <summary>Counts one candidate the content policy stopped.</summary>
    /// <param name="stage">The moderation stage's wire name; the closed vocabulary, never a text.</param>
    /// <exception cref="ArgumentException"><paramref name="stage"/> is blank.</exception>
    public void ModerationFlagged(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        _moderationFlags.Add(1, new KeyValuePair<string, object?>(StageTagName, stage));
    }

    /// <summary>Counts one moderation check that could not complete, whatever the fail mode then decides.</summary>
    public void ModerationUnavailable() => _moderationUnavailable.Add(1);

    /// <summary>
    /// Counts the candidates a variety rule blocked, in one add, so a batch the template rule stopped is one
    /// instrument update rather than one per candidate.
    /// </summary>
    /// <param name="rule">
    /// The rule that blocked them: <see cref="IdeaRejectionReason.VarietyTripleRepeat"/> or
    /// <see cref="IdeaRejectionReason.VarietyTemplateRepeat"/>, and nothing else (the closed
    /// <see cref="VarietyRuleNames"/> set — the value is a rule code, never idea text, D19).
    /// </param>
    /// <param name="count">How many candidates the rule blocked; must be positive.</param>
    /// <exception cref="ArgumentException"><paramref name="rule"/> is blank or outside <see cref="VarietyRuleNames"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    public void VarietyRejections(string rule, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (!VarietyRuleNames.Contains(rule))
        {
            throw new ArgumentException("The variety rule is outside the closed vocabulary.", nameof(rule));
        }

        _varietyRejections.Add(count, new KeyValuePair<string, object?>(RuleTagName, rule));
    }

    /// <summary>Counts the candidates the variety guard penalised without blocking.</summary>
    /// <param name="count">How many candidates carried a positive penalty; must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    public void VarietyPenalties(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        _varietyPenalties.Add(count);
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
