using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// <see cref="AutopilotModerationGate"/> (02.8.2-T2): the per-category threshold rules of decision 5, the
/// fail-closed paths, the chunking that keeps a D22 segment address, the one evidence row per failing chunk and
/// the counter T3 calibrates with. Transport-free: a scripted moderator and a recording event writer (D20).
/// </summary>
public sealed class AutopilotModerationGateTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid EpisodeId = Guid.NewGuid();
    private static readonly Guid VersionId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AutopilotModerationGate_Clean_Passes()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(gate, Request());

        verdict.Flagged.ShouldBeFalse();
        verdict.Categories.ShouldBeEmpty("a pass carries no category: the caller reads Flagged");
        verdict.UnavailableDetail.ShouldBeNull();
        verdict.FlaggedSegments.ShouldBeEmpty();
        moderator.Checks.Count.ShouldBe(1);
        moderator.Stages.ShouldAllBe(stage => stage == ModerationStage.AutopilotGate);
        moderator.Checks[0].ShouldBe(["Title", "Hook", "Para one.", "Para two.", "Ending", "meta text", "scene text"]);
        events.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task AutopilotModerationGate_ScoreAboveThresholdButNotFlagged_Fails()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Verdict(flagged: false, Scores(("violence/graphic", 0.4)));
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(gate, SingleTextRequest());

        verdict.Flagged.ShouldBeTrue("0.4 is above the 0.3 ceiling even though the provider did not flag it");
        verdict.Categories.ShouldBe(["violence/graphic"]);
        verdict.FlaggedSegments.ShouldBe(["title"]);
        events.Items.Count.ShouldBe(1);
        events.Items[0].Categories.ShouldBe(["violence/graphic"]);
    }

    [Fact]
    public async Task AutopilotModerationGate_BlockedCategory_Fails()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Verdict(flagged: true, Scores(("sexual/minors", 0.2)), "sexual/minors");
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(gate, SingleTextRequest());

        verdict.Flagged.ShouldBeTrue("a blocked category fails whatever its score");
        verdict.Categories.ShouldBe(["sexual/minors"]);
        events.Items.Count.ShouldBe(1);
        events.Items[0].Stage.ShouldBe(ModerationStage.AutopilotGate);
        events.Items[0].Source.ShouldBe(ModerationSource.OpenaiText);
        events.Items[0].OwnText!.Length.ShouldBeLessThanOrEqualTo(300);
    }

    [Fact]
    public async Task AutopilotModerationGate_FlaggedCategoryWithoutCeiling_Fails()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Verdict(flagged: true, Scores(("self-harm/intent", 0.1)), "self-harm/intent");
        var gate = Gate(moderator, new RecordingEvents(), metrics);

        var verdict = await CheckAsync(gate, SingleTextRequest());

        verdict.Flagged.ShouldBeTrue("a category the profile never decided a ceiling for must not pass silently");
        verdict.Categories.ShouldBe(["self-harm/intent"]);
    }

    [Fact]
    public async Task AutopilotModerationGate_FlaggedViolenceBelowItsCeiling_Passes()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Verdict(flagged: true, Scores(("violence", 0.55)), "violence");
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(gate, SingleTextRequest());

        verdict.Flagged.ShouldBeFalse("0.55 is under the 0.6 ceiling: horror fiction must remain possible");
        verdict.Categories.ShouldBeEmpty();
        events.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task AutopilotModerationGate_FlaggedWithoutCategories_Fails()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Verdict(flagged: true, Scores());
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(gate, SingleTextRequest());

        verdict.Flagged.ShouldBeTrue("a flag with no category is unclassified and fails closed");
        verdict.Categories.ShouldBeEmpty();
        events.Items.Count.ShouldBe(1);
        events.Items[0].Categories.ShouldBeEmpty();
    }

    [Fact]
    public async Task AutopilotModerationGate_ModeratorUnavailable_FailsClosed()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Unavailable();
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(gate, Request());

        verdict.Flagged.ShouldBeTrue("nothing checked is never a pass");
        verdict.UnavailableDetail.ShouldBe(GateDetailCodes.ModeratorUnavailable);
        verdict.Categories.ShouldBeEmpty();
        verdict.FlaggedSegments.ShouldBeEmpty();
        events.Items.ShouldBeEmpty("nothing was classified, so there is no evidence to write");
        moderator.Checks.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AutopilotModerationGate_NothingToModerate_FailsClosed()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);
        var blank = new AutopilotContentGateRequest(
            UserId,
            EpisodeId,
            VersionId,
            Language.Vi,
            StoryTextSegments.Create("  ", null, [""], null, null),
            ["", "   "],
            "dark-folklore-ink",
            AutopilotGateProfile.Defaults);

        var verdict = await CheckAsync(gate, blank);

        verdict.Flagged.ShouldBeTrue();
        verdict.UnavailableDetail.ShouldBe(GateDetailCodes.FieldAbsent);
        moderator.Checks.ShouldBeEmpty("nothing to send: the gate fails closed without a call");
        events.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task AutopilotModerationGate_ChunksLongText_OnParagraphBoundary()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var gate = Gate(moderator, new RecordingEvents(), metrics, maxCharsPerChunk: ModerationOptions.MinCharsPerChunk);

        var first = "First paragraph. " + new string('a', 200);
        var second = "Second paragraph. " + new string('b', 200);
        var third = "Third paragraph. " + new string('c', 200);
        var long3Paragraphs = $"{first}\n\n{second}\n\n{third}";
        long3Paragraphs.Length.ShouldBeGreaterThan(ModerationOptions.MinCharsPerChunk);

        var verdict = await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                Language.Vi,
                StoryTextSegments.Create(long3Paragraphs, null, [], null, null),
                [],
                "dark-folklore-ink",
                AutopilotGateProfile.Defaults));

        verdict.Flagged.ShouldBeFalse();
        var chunks = moderator.Checks[0];
        chunks.Count.ShouldBe(2);
        chunks[0].ShouldEndWith(second, Case.Sensitive, "the cut lands on the last paragraph break inside the cap, never mid-word");
        chunks[1].ShouldEndWith(third);
        chunks[1].ShouldStartWith(chunks[0][^200..], Case.Sensitive, "the pieces overlap by the port's 200 characters, so a phrase over the cut is whole in one");
        chunks.ShouldAllBe(chunk => chunk.Length <= ModerationOptions.MinCharsPerChunk);
    }

    [Fact]
    public async Task AutopilotModerationGate_ChunksNeverExceedTheCapOrSplitAGrapheme()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var gate = Gate(moderator, new RecordingEvents(), metrics, maxCharsPerChunk: ModerationOptions.MinCharsPerChunk);

        var withPair = new string('x', ModerationOptions.MinCharsPerChunk - 1) + "\U0001F600" + new string('y', 20);
        var withCombining = new string('z', ModerationOptions.MinCharsPerChunk - 1) + "e\u0301" + new string('w', 20);

        await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                Language.Vi,
                StoryTextSegments.Create(withPair, withCombining, [], null, null),
                [],
                "dark-folklore-ink",
                AutopilotGateProfile.Defaults));

        var chunks = moderator.Checks[0];
        chunks.ShouldAllBe(chunk => chunk.Length <= ModerationOptions.MinCharsPerChunk);
        chunks.ShouldAllBe(chunk => !char.IsHighSurrogate(chunk[chunk.Length - 1]) && !char.IsLowSurrogate(chunk[0]));
        chunks.ShouldContain(chunk => chunk.Contains("\U0001F600", StringComparison.Ordinal), "the pair stays whole");
        chunks.ShouldContain(chunk => chunk.Contains("e\u0301", StringComparison.Ordinal), "the combining sequence stays whole");
    }

    [Fact]
    public async Task AutopilotModerationGate_WritesOneEventPerFlaggedChunk()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator()
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate");
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics, maxCharsPerChunk: ModerationOptions.MinCharsPerChunk);
        var first = "First paragraph. " + new string('a', 200);
        var second = "Second paragraph. " + new string('b', 200);

        var verdict = await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                Language.Vi,
                StoryTextSegments.Create(null, null, [first, second], null, null),
                [],
                "dark-folklore-ink",
                AutopilotGateProfile.Defaults));

        verdict.Flagged.ShouldBeTrue();
        verdict.FlaggedSegments.ShouldBe(["p:0", "p:1"]);
        moderator.Checks.Count.ShouldBe(3, "one union check, then one per chunk to attribute");
        events.Items.Count.ShouldBe(2, "one row per failing chunk");
        events.Items[0].OwnText.ShouldBe(first);
        events.Items[1].OwnText.ShouldBe(second);
        events.Items.ShouldAllBe(item => item.EpisodeId == EpisodeId && item.UserId == UserId);
        events.Items.ShouldAllBe(item => item.Categories.SequenceEqual(new[] { "hate" }));
    }

    [Fact]
    public async Task AutopilotModerationGate_ReCheckAttributesOnlyTheFlaggedChunk()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator()
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Clean()
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Clean();
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(
            gate,
            Request(title: "Title", hook: "Hook", paragraphs: ["Para one.", "Para two."], ending: "Ending"));

        verdict.Flagged.ShouldBeTrue();
        verdict.FlaggedSegments.ShouldBe(["hook"]);
        events.Items.Count.ShouldBe(1);
        events.Items[0].OwnText.ShouldBe("Hook");
    }

    [Fact]
    public async Task AutopilotModerationGate_UnavailableDuringReCheck_KeepsFailClosed()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator()
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Unavailable();
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(
            gate,
            Request(title: "Title", hook: "Hook", paragraphs: ["Para one.", "Para two."], ending: "Ending"));

        verdict.Flagged.ShouldBeTrue("the union verdict failed; an attribution outage does not undo it");
        verdict.UnavailableDetail.ShouldBeNull();
        verdict.FlaggedSegments.ShouldBe(["title"]);
        events.Items.Count.ShouldBe(1, "the chunks attributed before the outage keep their row");
    }

    [Fact]
    public async Task AutopilotModerationGate_Cancelled_Propagates()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var gate = Gate(moderator, new RecordingEvents(), metrics);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await gate.CheckAsync(Request(), cancelled.Token));
    }

    [Fact]
    public async Task AutopilotModerationGate_LogsNoContent()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Verdict(flagged: true, Scores(("hate", 0.9)), "hate");
        var log = new RecordingLogger();
        var gate = Gate(moderator, new RecordingEvents(), metrics, log);

        await CheckAsync(
            gate,
            Request(
                title: "SECRET-TITLE",
                hook: "SECRET-HOOK",
                paragraphs: ["SECRET-BODY"],
                ending: "SECRET-ENDING",
                meta: "SECRET-META",
                scenes: ["SECRET-SCENE"]));

        log.Lines.ShouldNotBeEmpty();
        log.Lines.ShouldAllBe(line => !line.Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutopilotModerationGate_CountsEachFlaggedCategoryPerFailingChunk()
    {
        using var listener = new FlagListener();
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator()
            .Verdict(flagged: true, Scores(("hate", 0.9), ("violence", 0.9)), "hate", "violence")
            .Verdict(flagged: true, Scores(("hate", 0.9), ("violence", 0.9)), "hate", "violence")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate");
        var gate = Gate(moderator, new RecordingEvents(), metrics);
        var first = "First paragraph. " + new string('a', 200);
        var second = "Second paragraph. " + new string('b', 200);

        await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                Language.Vi,
                StoryTextSegments.Create(null, null, [first, second], null, null),
                [],
                "dark-folklore-ink",
                AutopilotGateProfile.Defaults));

        listener.Flags.Where(flag => flag.Category == "hate").Sum(flag => flag.Value).ShouldBe(2);
        listener.Flags.Where(flag => flag.Category == "violence").Sum(flag => flag.Value).ShouldBe(1);
        listener.Flags.ShouldAllBe(flag => flag.Stage == "autopilot_gate");
    }

    private static AutopilotContentGateRequest Request(
        string? title = "Title",
        string? hook = "Hook",
        IReadOnlyList<string>? paragraphs = null,
        string? ending = "Ending",
        string? meta = "meta text",
        IReadOnlyList<string>? scenes = null) =>
        new(
            UserId,
            EpisodeId,
            VersionId,
            Language.Vi,
            StoryTextSegments.Create(title, hook, paragraphs ?? ["Para one.", "Para two."], ending, meta),
            scenes ?? ["scene text"],
            "dark-folklore-ink",
            AutopilotGateProfile.Defaults);

    private static AutopilotContentGateRequest SingleTextRequest() =>
        new(
            UserId,
            EpisodeId,
            VersionId,
            Language.Vi,
            StoryTextSegments.Create("Title", null, [], null, null),
            [],
            "dark-folklore-ink",
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
        ContentGateMetrics metrics,
        ILogger<AutopilotModerationGate>? log = null,
        int maxCharsPerChunk = 8000) =>
        new(
            moderator,
            events,
            new ModerationOptions { MaxCharsPerChunk = maxCharsPerChunk },
            metrics,
            log ?? NullLogger<AutopilotModerationGate>.Instance);

    /// <summary>A moderator whose verdicts are queued: a stage with nothing left answers clean, as the port does.</summary>
    private sealed class ScriptedModerator : IContentModerator
    {
        private readonly Queue<Func<ModerationResult>> _script = new();

        /// <summary>Every check received, in order (test data only).</summary>
        public List<IReadOnlyList<string>> Checks { get; } = [];

        /// <summary>The stage of every check received.</summary>
        public List<ModerationStage> Stages { get; } = [];

        public ScriptedModerator Clean()
        {
            _script.Enqueue(() => ModerationResult.Clean);

            return this;
        }

        public ScriptedModerator Unavailable()
        {
            _script.Enqueue(() => throw new ModerationUnavailableException(503));

            return this;
        }

        public ScriptedModerator Verdict(
            bool flagged,
            IReadOnlyDictionary<string, double> scores,
            params string[] categories)
        {
            _script.Enqueue(() => new ModerationResult(flagged, categories, scores));

            return this;
        }

        public ValueTask<ModerationResult> CheckAsync(
            IReadOnlyList<string> texts,
            ModerationStage stage,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Checks.Add([.. texts]);
            Stages.Add(stage);

            return ValueTask.FromResult(_script.Count > 0 ? _script.Dequeue()() : ModerationResult.Clean);
        }
    }

    /// <summary>The rows the gate wrote, in order (02 README D12); the excerpt the domain would store is ownText.</summary>
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

    /// <summary>Captures every formatted log line, which is what a log query would see (D19).</summary>
    private sealed class RecordingLogger : ILogger<AutopilotModerationGate>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Lines.AddRange(values.Select(value => value.Value?.ToString() ?? string.Empty));
            }
        }
    }

    /// <summary>Reads the gate's counter off the autopilot meter, per category and summed per category.</summary>
    private sealed class FlagListener : IDisposable
    {
        private readonly MeterListener _listener = new();

        public FlagListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, ContentGateMetrics.MeterName, StringComparison.Ordinal)
                    && string.Equals(instrument.Name, ContentGateMetrics.FlagsTotalName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            {
                string? stage = null;
                string? category = null;

                foreach (var (key, tagValue) in tags)
                {
                    if (string.Equals(key, ContentGateMetrics.StageTagName, StringComparison.Ordinal))
                    {
                        stage = tagValue as string;
                    }
                    else if (string.Equals(key, ContentGateMetrics.CategoryTagName, StringComparison.Ordinal))
                    {
                        category = tagValue as string;
                    }
                }

                Flags.Add((stage, category, value));
            });
            _listener.Start();
        }

        /// <summary>One measurement per flagged category: (stage, category, value).</summary>
        public List<(string? Stage, string? Category, long Value)> Flags { get; } = [];

        public void Dispose() => _listener.Dispose();
    }
}
