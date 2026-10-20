using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// QA probes of <see cref="AutopilotModerationGate"/> (02.8.2-T2, written by the QA agent, not by the
/// implementer): a paragraph far beyond the chunk cap, a scene-less story, the scene index inside
/// <c>scene:&lt;i&gt;</c>, and the boundary where a blocked category sits under the provider's own flag threshold.
/// </summary>
public sealed class AutopilotModerationGateQaTests
{
    private const int Cap = ModerationOptions.MinCharsPerChunk;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid EpisodeId = Guid.NewGuid();
    private static readonly Guid VersionId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A 20,000-character paragraph with no whitespace at all is the worst case of the failure table's
    /// "single paragraph longer than the limit": every piece stays at or under the cap, the pieces cover the text
    /// without a gap (each next piece repeats the previous tail by the port's overlap), and no surrogate or
    /// combining sequence is cut.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGateQa_TwentyThousandCharParagraph_HardSplitsAtTheCapWithoutAGap()
    {
        // A letter at every position and no whitespace at all: the split must cut on the grapheme boundary it can
        // find, which is the hard limit. The letters come from a long-period sequence (not index % 26, whose 26-char
        // period would make a 500-char window match at many offsets) so IndexOf pinpoints one position only.
        var state = 12_345U;
        var paragraph = string.Create(20_000, state, static (span, seed) =>
        {
            var current = seed;
            for (var index = 0; index < span.Length; index++)
            {
                current = (current * 1_103_515_245U) + 12_345U;
                span[index] = (char)('a' + ((current >> 16) % 26));
            }
        });

        var moderator = new ScriptedModerator().Clean();
        using var metrics = new ContentGateMetrics();
        var gate = Gate(moderator, new RecordingEvents(), metrics);
        var request = new AutopilotContentGateRequest(
            UserId,
            EpisodeId,
            VersionId,
            StoryTextSegments.Create(null, null, [paragraph], null, null),
            [],
            AutopilotGateProfile.Defaults);

        var verdict = await CheckAsync(gate, request);

        verdict.Flagged.ShouldBeFalse();
        var chunks = moderator.Checks.ShouldHaveSingleItem();
        moderator.Checks.Count.ShouldBe(1, "one batch call, because the clean verdict needs no attribution");

        // The cap is honoured per piece, and the plan for this input is fixed: 20,000 characters, no whitespace, so
        // every piece is the full 500 and every next one starts 300 characters later (the 500 cap less the 200
        // overlap). A plan with more pieces means the split fell back to crawling, which sends the provider
        // near-duplicate fragments (measured: 166 pieces, lengths 500 x33, 200,198,...,2, 500 x32, 400 before the
        // fix).
        chunks.ShouldAllBe(chunk => chunk.Length <= Cap, $"lengths=[{Lengths(chunks)}]");
        chunks.Count.ShouldBe(66, $"a 20,000-character paragraph at a 500 cap is 66 pieces; lengths=[{Lengths(chunks)}]");
        chunks.Take(65).ShouldAllBe(chunk => chunk.Length == Cap, $"lengths=[{Lengths(chunks)}]");
        chunks[^1].Length.ShouldBe(paragraph.Length - (65 * (Cap - 200)), $"lengths=[{Lengths(chunks)}]");

        // Coverage without assuming the split arithmetic: every piece is our own text, the first piece is the
        // paragraph's head, the last its tail, neighbours overlap by the port's 200 characters, and the overlapping
        // windows add up to exactly the paragraph — so a gap (a place a flag could hide across) shows up here.
        var offsets = new List<int>();
        foreach (var chunk in chunks)
        {
            chunk.Length.ShouldBeGreaterThan(0);
            offsets.Add(paragraph.IndexOf(chunk, StringComparison.Ordinal));
        }

        offsets.ShouldAllBe(offset => offset >= 0, "a piece must be a contiguous slice of our own text");
        for (var index = 0; index < chunks.Count - 1; index++)
        {
            (offsets[index + 1] - offsets[index]).ShouldBe(
                chunks[index].Length - 200,
                $"index={index}/{chunks.Count} lengths=[{Lengths(chunks)}] offsets=[{string.Join(",", offsets)}]");
        }

        (offsets[^1] + chunks[^1].Length).ShouldBe(paragraph.Length, "the last piece must reach the end");
    }

    private static string Lengths(IReadOnlyList<string> chunks) =>
        string.Join(",", chunks.Select(chunk => chunk.Length));

    /// <summary>
    /// A story with no keywords (the failure table's "keyword list empty" row) moderates the narration and the
    /// metadata only, and the gate still passes a clean verdict. The narration is one packed text — a short story
    /// costs one request, not one per paragraph (SEC-02) — and the metadata stays its own text, so a flag on the
    /// description never reads as a flag on the prose.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGateQa_NoScenePrompts_ChecksTheStoryAlone()
    {
        var moderator = new ScriptedModerator().Clean();
        using var metrics = new ContentGateMetrics();
        var gate = Gate(moderator, new RecordingEvents(), metrics);
        var request = new AutopilotContentGateRequest(
            UserId,
            EpisodeId,
            VersionId,
            StoryTextSegments.Create("Title", "Hook", ["Para one.", "Para two."], "Ending", "meta text"),
            [],
            AutopilotGateProfile.Defaults);

        var verdict = await CheckAsync(gate, request);

        verdict.Flagged.ShouldBeFalse();
        moderator.Checks.Count.ShouldBe(1);
        moderator.Checks[0].ShouldBe(["Title\n\nHook\n\nPara one.\n\nPara two.\n\nEnding", "meta text"]);
    }

    /// <summary>
    /// A blank scene prompt does not renumber the scenes after it: the flagged prompt is `scene:1`, the address
    /// the owner's editor and the run log agree on.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGateQa_BlankScenePromptKeepsTheKeywordIndex()
    {
        var moderator = new ScriptedModerator()
            .FlagWhen(texts => texts.Any(text => text.Contains("SECOND-SCENE", StringComparison.Ordinal)), "hate");
        var events = new RecordingEvents();
        using var metrics = new ContentGateMetrics();
        var gate = Gate(moderator, events, metrics);
        var request = new AutopilotContentGateRequest(
            UserId,
            EpisodeId,
            VersionId,
            StoryTextSegments.Create("Title", null, [], null, null),
            ["", "SECOND-SCENE"],
            AutopilotGateProfile.Defaults);

        var verdict = await CheckAsync(gate, request);

        verdict.Flagged.ShouldBeTrue();
        verdict.FlaggedSegments.ShouldBe(["scene:1"]);
        events.Items.ShouldHaveSingleItem().OwnText.ShouldBe("SECOND-SCENE");
    }

    /// <summary>
    /// A blocked category whose score stays under the provider's own flag threshold and which the provider did not
    /// flag is a pass: the shipped gate fails on a flag or on a breached ceiling, and moderation/1's own flag
    /// threshold is the provider's. Pinned because it is the one boundary where "blocked" does not mean "always
    /// fails", so a future tightening is a deliberate change (decision 5 and <see cref="ModerationThresholds"/>).
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGateQa_BlockedCategoryBelowTheProviderThreshold_IsAPass()
    {
        var moderator = new ScriptedModerator().Verdict(
            flagged: false,
            Scores(("sexual/minors", 0.2)));
        var events = new RecordingEvents();
        using var metrics = new ContentGateMetrics();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(gate, SingleTextRequest());

        verdict.Flagged.ShouldBeFalse("nothing was flagged and no ceiling was breached");
        events.Items.ShouldBeEmpty();
    }

    private static AutopilotContentGateRequest SingleTextRequest() => new(
        UserId,
        EpisodeId,
        VersionId,
        StoryTextSegments.Create("Title", null, [], null, null),
        [],
        AutopilotGateProfile.Defaults);

    private static async Task<ContentGateVerdict> CheckAsync(AutopilotModerationGate gate, AutopilotContentGateRequest request)
    {
        var result = await gate.CheckAsync(request, Ct);

        result.IsError.ShouldBeFalse();
        return result.Value;
    }

    private static Dictionary<string, double> Scores(params (string Category, double Score)[] scores) =>
        scores.ToDictionary(entry => entry.Category, entry => entry.Score, StringComparer.Ordinal);

    private static AutopilotModerationGate Gate(
        IContentModerator moderator,
        IModerationEventWriter events,
        ContentGateMetrics metrics) =>
        new(
            moderator,
            events,
            new ModerationOptions { MaxCharsPerChunk = Cap },
            metrics,
            NullLogger<AutopilotModerationGate>.Instance);

    /// <summary>A moderator whose verdicts are queued, with one text matcher for the attribution pass.</summary>
    private sealed class ScriptedModerator : IContentModerator
    {
        private readonly Queue<Func<ModerationResult>> _script = new();
        private Func<IReadOnlyList<string>, bool>? _matches;
        private Func<ModerationResult>? _matchVerdict;

        public List<IReadOnlyList<string>> Checks { get; } = [];

        public ScriptedModerator Clean()
        {
            _script.Enqueue(() => ModerationResult.Clean);

            return this;
        }

        public ScriptedModerator Verdict(bool flagged, IReadOnlyDictionary<string, double> scores, params string[] categories)
        {
            _script.Enqueue(() => new ModerationResult(flagged, categories, scores));

            return this;
        }

        public ScriptedModerator FlagWhen(Func<IReadOnlyList<string>, bool> matches, params string[] categories)
        {
            _matches = matches;
            _matchVerdict = () => new ModerationResult(
                true,
                categories,
                categories.ToDictionary(category => category, _ => 0.99, StringComparer.Ordinal));

            return this;
        }

        public ValueTask<ModerationResult> CheckAsync(
            IReadOnlyList<string> texts,
            ModerationStage stage,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Checks.Add([.. texts]);

            if (_script.Count > 0)
            {
                return ValueTask.FromResult(_script.Dequeue()());
            }

            if (_matchVerdict is not null && texts.Any(text => _matches!([text])))
            {
                return ValueTask.FromResult(_matchVerdict());
            }

            return ValueTask.FromResult(ModerationResult.Clean);
        }
    }

    /// <summary>The rows the gate wrote, in order: the excerpt the domain would keep is <c>OwnText</c>.</summary>
    private sealed class RecordingEvents : IModerationEventWriter
    {
        public List<Event> Items { get; } = [];

        public Task WriteAsync(
            Guid userId,
            Guid? episodeId,
            ModerationSource source,
            ModerationStage stage,
            IReadOnlyCollection<string> categories,
            string? ownText,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Items.Add(new Event(userId, episodeId, source, stage, [.. categories], ownText));

            return Task.CompletedTask;
        }
    }

    private sealed record Event(
        Guid UserId,
        Guid? EpisodeId,
        ModerationSource Source,
        ModerationStage Stage,
        string[] Categories,
        string? OwnText);
}
