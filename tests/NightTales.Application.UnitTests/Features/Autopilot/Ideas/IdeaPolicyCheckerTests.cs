using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Ideas;

/// <summary>
/// The content-policy pre-check's rules (02.8.1-T2 task "Content policy" and "Failure handling and edge cases"):
/// the banned-theme refusal that never pays for a moderation call, the moderation verdict and its evidence row,
/// and the two halves of <c>Autopilot:Ideas:Policy:FailClosed</c> when the endpoint is down. The moderator and the
/// evidence writer are hand-written, so what is asserted is the mapping and the call count, not the endpoint.
/// </summary>
public sealed class IdeaPolicyCheckerTests
{
    /// <summary>
    /// A banned theme is refused by the free local check, and the candidate never reaches the moderator: the row
    /// carries the themes it matched so the run log can say why.
    /// </summary>
    [Fact]
    public async Task BannedThemeMatch_RejectsWithTheMatchedThemes_AndSkipsModeration()
    {
        var candidate = Candidate(logline: "Mot cau chuyen ve tu tu.");
        var moderator = new StubModerator();

        var result = await CheckAsync(
            [candidate],
            new IdeaPolicyContext("supernatural", ["tu tu"]),
            moderator: moderator);

        var verdict = Verdict(result, 0);
        verdict.RejectReason.ShouldBe(IdeaRejectionReason.PolicyBannedTheme);
        verdict.MatchedBannedThemes.ShouldBe(["tu tu"]);
        verdict.RequiresResearch.ShouldBeFalse();
        moderator.Calls.ShouldBeEmpty();
    }

    /// <summary>A flagged candidate is refused as <c>policy_moderated</c>, with one <c>brief</c> evidence row.</summary>
    [Fact]
    public async Task FlaggedCandidate_RejectsAndWritesTheBriefEvidenceRow()
    {
        var candidate = Candidate(logline: "A logline.", hook: "A hook.");
        var moderator = new StubModerator(Outcome.Flagged("sexual"));
        var events = new StubEventWriter();

        var result = await CheckAsync([candidate], Context, moderator: moderator, events: events);

        Verdict(result, 0).RejectReason.ShouldBe(IdeaRejectionReason.PolicyModerated);
        var write = events.Writes.Single();
        write.Stage.ShouldBe(ModerationStage.Brief);
        write.Source.ShouldBe(ModerationSource.OpenaiText);
        write.Categories.ShouldBe(["sexual"]);
        write.OwnText.ShouldNotBeNull();
        write.OwnText.ShouldContain(candidate.Logline);
        write.OwnText.ShouldContain(candidate.Hook);
        write.OwnText!.Length.ShouldBeLessThanOrEqualTo(300);
    }

    /// <summary>The moderator is asked about the two texts of the brief, at the brief stage, once per candidate.</summary>
    [Fact]
    public async Task ModeratorIsAskedAboutTheLoglineAndTheHookOncePerCandidate()
    {
        var moderator = new StubModerator(Outcome.Clean, Outcome.Clean);

        await CheckAsync(
            [Candidate(logline: "First.", hook: "First hook."), Candidate(logline: "Second.", hook: "Second hook.")],
            Context,
            moderator: moderator);

        moderator.Calls.Count.ShouldBe(2);
        moderator.Calls[0].Texts.ShouldBe(["First.", "First hook."]);
        moderator.Calls[0].Stage.ShouldBe(ModerationStage.Brief);
        moderator.Calls[1].Texts.ShouldBe(["Second.", "Second hook."]);
    }

    /// <summary>A clean candidate is cleared with no reason and no research flag, and writes no evidence.</summary>
    [Fact]
    public async Task CleanCandidate_IsClearedAndWritesNothing()
    {
        var events = new StubEventWriter();

        var result = await CheckAsync([Candidate()], Context, moderator: new StubModerator(Outcome.Clean), events: events);

        var verdict = Verdict(result, 0);
        verdict.RejectReason.ShouldBeNull();
        verdict.RequiresResearch.ShouldBeFalse();
        verdict.MatchedBannedThemes.ShouldBeEmpty();
        events.Writes.ShouldBeEmpty();
    }

    /// <summary>
    /// The switch off: no moderation call is bought at all, and the banned-theme check still runs — it is the free
    /// half of the policy and the one the shipped default keeps.
    /// </summary>
    [Fact]
    public async Task ModerationDisabled_SkipsTheModeratorButKeepsTheBannedThemeCheck()
    {
        var moderator = new StubModerator();
        var options = new IdeaPolicyOptions { ModerationEnabled = false };

        var result = await CheckAsync(
            [Candidate(), Candidate(logline: "Mot cau chuyen ve tu tu.")],
            new IdeaPolicyContext("supernatural", ["tu tu"]),
            moderator: moderator,
            options: options);

        moderator.Calls.ShouldBeEmpty();
        Verdict(result, 0).RejectReason.ShouldBeNull();
        Verdict(result, 1).RejectReason.ShouldBe(IdeaRejectionReason.PolicyBannedTheme);
    }

    /// <summary>
    /// An empty batch buys nothing and answers nothing: there is no candidate to check and no run text to send, so
    /// the caller's zero-candidate path never pays for a moderation call.
    /// </summary>
    [Fact]
    public async Task EmptyBatch_AsksNothingAndAnswersNothing()
    {
        var moderator = new StubModerator();

        var result = await CheckAsync([], Context, moderator: moderator);

        moderator.Calls.ShouldBeEmpty();
        result.ByIdeaId.ShouldBeEmpty();
    }

    /// <summary>
    /// The endpoint is down and the deployment clears (the shipped default): every candidate is cleared, the batch
    /// remembers the outage so the remaining candidates are not asked again, and no evidence row is invented —
    /// nothing was flagged, the counter and the log are what record the outage.
    /// </summary>
    [Fact]
    public async Task UnavailableEndpoint_FailClosedFalse_ClearsTheBatchAfterOneCall()
    {
        var moderator = new StubModerator(Outcome.Unavailable);
        var events = new StubEventWriter();

        var result = await CheckAsync([Candidate(), Candidate()], Context, moderator: moderator, events: events);

        moderator.Calls.Count.ShouldBe(1);
        Verdict(result, 0).RejectReason.ShouldBeNull();
        Verdict(result, 1).RejectReason.ShouldBeNull();
        events.Writes.ShouldBeEmpty();
    }

    /// <summary>
    /// The endpoint is down and the deployment fails closed: the candidates are refused as <c>policy_moderated</c>
    /// — the same code a flagged one gets, because nothing unchecked may be bought — still without evidence rows.
    /// </summary>
    [Fact]
    public async Task UnavailableEndpoint_FailClosedTrue_RejectsTheWholeBatch()
    {
        var moderator = new StubModerator(Outcome.Unavailable);
        var events = new StubEventWriter();
        var options = new IdeaPolicyOptions { FailClosed = true };

        var result = await CheckAsync(
            [Candidate(), Candidate()],
            Context,
            moderator: moderator,
            events: events,
            options: options);

        moderator.Calls.Count.ShouldBe(1);
        Verdict(result, 0).RejectReason.ShouldBe(IdeaRejectionReason.PolicyModerated);
        Verdict(result, 1).RejectReason.ShouldBe(IdeaRejectionReason.PolicyModerated);
        events.Writes.ShouldBeEmpty();
    }

    /// <summary>
    /// True crime with the framing lexicon in the text is the one case that needs the owner's fact sheet: the
    /// candidate is still eligible, it is only marked.
    /// </summary>
    [Fact]
    public async Task TrueCrimeFraming_SetsRequiresResearchWithoutRejecting()
    {
        var options = new IdeaPolicyOptions { FramingLexicon = ["based on a true story"] };

        var result = await CheckAsync(
            [Candidate(logline: "A case based on a true story.")],
            new IdeaPolicyContext(IdeaPolicyContext.TrueCrimeTopic, []),
            moderator: new StubModerator(Outcome.Clean),
            options: options);

        var verdict = Verdict(result, 0);
        verdict.RequiresResearch.ShouldBeTrue();
        verdict.RejectReason.ShouldBeNull();
    }

    /// <summary>
    /// The flag never leaks out of true crime, and it needs the framing text: a framed idea on another topic, and a
    /// true-crime idea that reads as fiction, both go without it.
    /// </summary>
    /// <param name="topic">The series' topic.</param>
    /// <param name="logline">The candidate's logline.</param>
    [Theory]
    [InlineData(IdeaPolicyContext.TrueCrimeTopic, "A haunted house on the hill.")]
    [InlineData("supernatural", "A case based on a true story.")]
    public async Task RequiresResearch_NeedsTrueCrimeAndTheFraming(string topic, string logline)
    {
        var options = new IdeaPolicyOptions { FramingLexicon = ["based on a true story"] };

        var result = await CheckAsync(
            [Candidate(logline: logline)],
            new IdeaPolicyContext(topic, []),
            moderator: new StubModerator(Outcome.Clean),
            options: options);

        Verdict(result, 0).RequiresResearch.ShouldBeFalse();
    }

    private static Task<PolicyBatchResult> CheckAsync(
        IReadOnlyList<IdeaCandidate> batch,
        IdeaPolicyContext context,
        StubModerator moderator,
        StubEventWriter? events = null,
        IdeaPolicyOptions? options = null)
    {
        var checker = new IdeaPolicyChecker(
            options ?? new IdeaPolicyOptions(),
            moderator,
            events ?? new StubEventWriter(),
            new IdeaMetrics(),
            NullLogger<IdeaPolicyChecker>.Instance);

        return checker
            .CheckAsync(
                [.. batch.Select(candidate => (Guid.NewGuid(), candidate))],
                context,
                Run(),
                CancellationToken.None)
            .AsTask();
    }

    private static PolicyVerdict Verdict(PolicyBatchResult result, int index) =>
        result.ByIdeaId.Values.ElementAt(index);

    private static IdeaPolicyContext Context => new("supernatural", []);

    private static IdeaCandidate Candidate(
        string logline = "A logline.",
        string hook = "A hook that opens on the first page.") =>
        new(
            "A title",
            logline,
            hook,
            "A setting.",
            StoryStructureTags.Empty,
            []);

    private static RunContext Run() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            new NightTales.Application.Abstractions.Money.ReservationId(Guid.NewGuid()),
            NightTales.Domain.Stories.Generation.GenerationTrigger.User,
            1,
            "1");

    /// <summary>One scripted outcome of the moderator: a verdict, or the endpoint being down.</summary>
    private sealed record Outcome(ModerationResult? Check, bool Down)
    {
        /// <summary>A clean verdict.</summary>
        internal static Outcome Clean { get; } = new(ModerationResult.Clean, false);

        /// <summary>A verdict that flags the given categories.</summary>
        /// <param name="categories">The flagged categories.</param>
        internal static Outcome Flagged(params string[] categories) =>
            new(new ModerationResult(true, categories, new Dictionary<string, double>()), false);

        /// <summary>The endpoint failed after its own retry budget.</summary>
        internal static Outcome Unavailable { get; } = new(null, true);
    }

    /// <summary>A hand-written moderator: the outcomes are queued and every call is kept.</summary>
    private sealed class StubModerator(params Outcome[] outcomes) : IContentModerator
    {
        private readonly Queue<Outcome> _outcomes = new(outcomes);

        /// <summary>Every check the policy asked for, in order.</summary>
        internal List<(IReadOnlyList<string> Texts, ModerationStage Stage)> Calls { get; } = [];

        /// <inheritdoc />
        public ValueTask<ModerationResult> CheckAsync(
            IReadOnlyList<string> texts,
            ModerationStage stage,
            CancellationToken ct)
        {
            Calls.Add((texts, stage));

            if (_outcomes.Count == 0)
            {
                throw new InvalidOperationException("The stub moderator has no outcome left: it was asked too often.");
            }

            var outcome = _outcomes.Dequeue();
            if (outcome.Down)
            {
                throw new ModerationUnavailableException(null);
            }

            return ValueTask.FromResult(outcome.Check!);
        }
    }

    /// <summary>A hand-written evidence writer: nothing reaches a database, every row is inspected.</summary>
    private sealed class StubEventWriter : IModerationEventWriter
    {
        /// <summary>Every row the policy asked for, in order.</summary>
        internal List<(Guid UserId, Guid? EpisodeId, ModerationSource Source, ModerationStage Stage, IReadOnlyCollection<string> Categories, string? OwnText)> Writes { get; } = [];

        /// <inheritdoc />
        public Task WriteAsync(
            Guid userId,
            Guid? episodeId,
            ModerationSource source,
            ModerationStage stage,
            IReadOnlyCollection<string> categories,
            string? ownText,
            CancellationToken ct)
        {
            Writes.Add((userId, episodeId, source, stage, categories, ownText));

            return Task.CompletedTask;
        }
    }
}
