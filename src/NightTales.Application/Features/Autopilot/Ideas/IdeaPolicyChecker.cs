using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Compliance;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The content-policy pre-check of one idea batch (02.8.1-T2 plan §4 U2, task Design "Content policy"): the
/// banned-theme backstop, then the moderator for the candidates it left standing, then the true-crime framing
/// flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic and free first.</b> The banned-theme check costs nothing, so it runs before anything is
/// asked of the network, and a candidate it stops never reaches the moderator: the series must not tell that
/// story at all, so a second opinion is not bought for it.
/// </para>
/// <para>
/// <b>One moderator check per surviving candidate</b>, over its logline and hook — the two texts of the brief
/// epoch (D12's <c>brief</c> stage) — because the endpoint answers one verdict per request and the refusal
/// evidence names one candidate. A flagged candidate is rejected, counted on
/// <c>moderation_flags_total{stage="brief"}</c> and written to <c>moderation_events</c> as one row carrying the
/// categories and the flagged text (the row keeps its first 300 characters).
/// </para>
/// <para>
/// <b>An unavailable endpoint is decided by configuration, not guessed.</b> It is counted on
/// <c>moderation_unavailable_total</c> either way; with the shipped <c>FailClosed = false</c> the batch is
/// cleared and the outage is visible in the counter, and with <c>FailClosed = true</c> every candidate is
/// rejected <see cref="IdeaRejectionReason.PolicyModerated"/> instead of buying ideas nothing checked. The
/// endpoint is asked once per batch at most: once it is unavailable, the rest of the batch takes the same
/// decision instead of repeating a call that just failed.
/// </para>
/// <para>
/// <b>Research is not a rejection.</b> A true-crime series' candidate that reads as real-world framing comes
/// back flagged for the story gate and the owner's fact-sheet approval (F31/F33); only a candidate that was
/// cleared is checked, because a rejected one never becomes a story.
/// </para>
/// <para>
/// <b>Nothing here is logged but counts.</b> Banned themes and idea text are owner content: the log lines carry
/// the run's id, the counts and the configured fail mode (D19).
/// </para>
/// </remarks>
/// <param name="options">The policy switches and the keyword data (<c>Autopilot:Ideas:Policy</c>).</param>
/// <param name="moderator">The content moderator (D12): free, and it meters its own <c>moderation</c> usage row.</param>
/// <param name="moderationEvents">The one writer of <c>moderation_events</c>: one row per flagged candidate.</param>
/// <param name="metrics">The flag and outage counters.</param>
/// <param name="logger">The outcome log of the pre-check.</param>
public sealed partial class IdeaPolicyChecker(
    IdeaPolicyOptions options,
    IContentModerator moderator,
    IModerationEventWriter moderationEvents,
    IdeaMetrics metrics,
    ILogger<IdeaPolicyChecker> logger) : IIdeaPolicyChecker
{
    /// <summary>The write budget of the refusal evidence, which must survive a caller that gave up (class 3).</summary>
    private static readonly TimeSpan ModerationWriteTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async ValueTask<PolicyBatchResult> CheckAsync(
        IReadOnlyList<(Guid IdeaId, IdeaCandidate Candidate)> batch,
        IdeaPolicyContext ctx,
        RunContext run,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(run);

        var verdicts = new Dictionary<Guid, PolicyVerdict>(batch.Count);
        var banned = 0;
        var flagged = 0;
        var research = 0;
        var unavailable = false;

        foreach (var (ideaId, candidate) in batch)
        {
            var text = PolicyText(candidate);

            var matched = IdeaBannedThemes.Match(text, ctx.BannedThemes);
            if (matched.Count > 0)
            {
                banned++;
                verdicts[ideaId] = new PolicyVerdict(IdeaRejectionReason.PolicyBannedTheme, false, matched);
                continue;
            }

            if (options.ModerationEnabled && !unavailable)
            {
                ModerationResult check;
                try
                {
                    check = await moderator
                        .CheckAsync([candidate.Logline, candidate.Hook], ModerationStage.Brief, ct)
                        .ConfigureAwait(false);
                }
                catch (ModerationUnavailableException)
                {
                    unavailable = true;
                    metrics.ModerationUnavailable();
                    LogUnavailable(run.GenerationRunId, options.FailClosed);

                    check = ModerationResult.Clean;
                }

                if (check.Flagged)
                {
                    flagged++;
                    metrics.ModerationFlagged(IdeaMetrics.BriefStage);
                    await WriteFlagAsync(run, candidate, check.Categories).ConfigureAwait(false);
                    verdicts[ideaId] = new PolicyVerdict(IdeaRejectionReason.PolicyModerated, false, []);
                    continue;
                }
            }

            if (unavailable && options.ModerationEnabled && options.FailClosed)
            {
                // The endpoint is down and this deployment must not buy ideas nothing checked: the candidate is
                // rejected exactly as a flagged one, but no evidence row is written — nothing was flagged, the
                // outage is what the counter and the log record.
                verdicts[ideaId] = new PolicyVerdict(IdeaRejectionReason.PolicyModerated, false, []);
                continue;
            }

            var needsResearch = RequiresResearch(text, ctx.Topic);
            research += needsResearch ? 1 : 0;
            verdicts[ideaId] = new PolicyVerdict(null, needsResearch, []);
        }

        LogBatch(run.GenerationRunId, batch.Count, banned, flagged, research, unavailable);

        return new PolicyBatchResult(verdicts);
    }

    /// <summary>
    /// The text the banned-theme and framing checks read: the candidate's title, logline, hook and setting
    /// (task Design "Content policy"), i.e. the lexical text plus the hook.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <returns>The joined text.</returns>
    private static string PolicyText(IdeaCandidate candidate) =>
        $"{candidate.Title} {candidate.Logline} {candidate.Hook} {candidate.Setting}";

    /// <summary>Whether a candidate of a true-crime series reads as real-world framing (F31/F33).</summary>
    /// <param name="text">The candidate's text.</param>
    /// <param name="topic">The series' topic; only <c>true_crime</c> arms the check.</param>
    /// <returns><see langword="true"/> when the story needs the owner's research first.</returns>
    private bool RequiresResearch(string text, string topic) =>
        string.Equals(topic, IdeaPolicyContext.TrueCrimeTopic, StringComparison.Ordinal)
        && IdeaTrueCrimeFraming.RequiresResearch(text, options.FramingLexicon, options.CrimeNouns);

    /// <summary>
    /// Writes the one <c>moderation_events</c> row of a flagged candidate (D12): the pre-check's own
    /// <c>brief</c> stage — distinct from the <c>ideas</c> stage a refusal of the run's paid call records — on
    /// its own budget, with the flagged text as the excerpt. The writer swallows its own failures, so the
    /// evidence never changes the run's outcome.
    /// </summary>
    /// <param name="run">The run the flag belongs to.</param>
    /// <param name="candidate">The flagged candidate.</param>
    /// <param name="categories">The flagged categories.</param>
    /// <returns>A task that completes when the row is saved or the failure is logged.</returns>
    private async ValueTask WriteFlagAsync(
        RunContext run,
        IdeaCandidate candidate,
        IReadOnlyCollection<string> categories)
    {
        using var terminal = new CancellationTokenSource(ModerationWriteTimeout);
        await moderationEvents
            .WriteAsync(
                run.UserId,
                run.EpisodeId,
                ModerationSource.OpenaiText,
                ModerationStage.Brief,
                categories,
                // D22: the excerpt is our own candidate text — the two texts the moderator was asked about —
                // never a source or a stored version. The domain keeps its first 300 characters.
                TextSafety.Clean($"{candidate.Logline} {candidate.Hook}"),
                terminal.Token)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 6343,
        Level = LogLevel.Warning,
        Message = "idea_policy_moderation_unavailable runId={RunId} failClosed={FailClosed}")]
    private partial void LogUnavailable(Guid runId, bool failClosed);

    [LoggerMessage(
        EventId = 6344,
        Level = LogLevel.Information,
        Message = "idea_policy_batch runId={RunId} candidates={Candidates} banned={Banned} flagged={Flagged} requiresResearch={RequiresResearch} unavailable={Unavailable}")]
    private partial void LogBatch(Guid runId, int candidates, int banned, int flagged, int requiresResearch, bool unavailable);
}
