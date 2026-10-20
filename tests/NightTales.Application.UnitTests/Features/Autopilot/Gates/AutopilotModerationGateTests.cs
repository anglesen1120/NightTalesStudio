using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// <see cref="AutopilotModerationGate"/> (02.8.2-T2): the per-category threshold rules of decision 5, the
/// fail-closed paths, the packing and splitting that keep a D22 segment address, the attribution rows and the
/// counter T3 calibrates with. Transport-free: a scripted moderator and a recording event writer (D20).
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
        moderator.Checks[0].ShouldBe(
            ["Title\n\nHook\n\nPara one.\n\nPara two.\n\nEnding", "meta text", "scene text"],
            "the narration is one packed text, the metadata and the scene prompt stay their own (SEC-02)");
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
            StoryTextSegments.Create("  ", null, [""], null, null),
            ["", "   "],
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
                StoryTextSegments.Create(long3Paragraphs, null, [], null, null),
                [],
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
                StoryTextSegments.Create(withPair, withCombining, [], null, null),
                [],
                AutopilotGateProfile.Defaults));

        var chunks = moderator.Checks[0];
        chunks.ShouldAllBe(chunk => chunk.Length <= ModerationOptions.MinCharsPerChunk);
        chunks.ShouldAllBe(chunk => !char.IsHighSurrogate(chunk[chunk.Length - 1]) && !char.IsLowSurrogate(chunk[0]));
        chunks.ShouldContain(chunk => chunk.Contains("\U0001F600", StringComparison.Ordinal), "the pair stays whole");
        chunks.ShouldContain(chunk => chunk.Contains("e\u0301", StringComparison.Ordinal), "the combining sequence stays whole");
    }

    /// <summary>
    /// A break near the start of the text is never taken (D2): taking it would leave the next start at or behind
    /// this one, because every next piece steps back by the 200-character overlap, and the split would then emit a
    /// near-duplicate piece per character for the rest of the window. Only a break in the last 20% of the window
    /// may end a piece, so this text is split into full-cap pieces instead.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_EarlyBreakFollowedByALongRun_StepsByTheOverlap()
    {
        const int cap = ModerationOptions.MinCharsPerChunk;
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var gate = Gate(moderator, new RecordingEvents(), metrics, maxCharsPerChunk: cap);
        var text = "short\n\n" + new string('x', 4000);

        var verdict = await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                StoryTextSegments.Create(text, null, [], null, null),
                [],
                AutopilotGateProfile.Defaults));

        verdict.Flagged.ShouldBeFalse();
        moderator.Checks.Count.ShouldBe(1, "one batch call: the pieces travel together");
        var chunks = moderator.Checks[0];
        chunks.ShouldAllBe(chunk => chunk.Length <= cap);
        chunks[0].ShouldStartWith("short\n\n", Case.Sensitive, "the early break is inside the first piece, not a cut");
        chunks.Count.ShouldBe(13, $"a break in the first characters must not cost extra pieces; lengths=[{Lengths(chunks)}]");
        chunks.Take(12).ShouldAllBe(chunk => chunk.Length == cap, $"lengths=[{Lengths(chunks)}]");
        chunks[^1].Length.ShouldBe(text.Length - (12 * (cap - 200)), $"lengths=[{Lengths(chunks)}]");

        // The pieces are runs of one repeated letter, so an occurrence search would find the first equal run
        // instead of the piece's own offset: the offsets are walked arithmetically instead.
        var offset = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            text.Substring(offset, chunks[index].Length).ShouldBe(chunks[index], $"piece {index} at {offset}");
            if (index < chunks.Count - 1)
            {
                offset += chunks[index].Length - 200;
            }
        }

        (offset + chunks[^1].Length).ShouldBe(text.Length, "the last piece reaches the end");
    }

    /// <summary>
    /// A long whitespace-free text (the failure table's "single paragraph longer than the limit") is hard-cut into
    /// full-cap pieces that overlap by 200 characters: one piece per 300 characters, never one per character (D2),
    /// and the final piece reaches the end of the text.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_SplitsALongWhitespaceFreeText_WithoutCrawling()
    {
        const int cap = ModerationOptions.MinCharsPerChunk;
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var gate = Gate(moderator, new RecordingEvents(), metrics, maxCharsPerChunk: cap);
        var text = new string('q', 2000);

        await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                StoryTextSegments.Create(null, null, [text], null, null),
                [],
                AutopilotGateProfile.Defaults));

        var chunks = moderator.Checks[0];
        chunks.Count.ShouldBe(6, $"500 characters of step per piece; lengths=[{Lengths(chunks)}]");
        chunks.ShouldAllBe(chunk => chunk.Length == cap, $"lengths=[{Lengths(chunks)}]");
        for (var index = 0; index < chunks.Count; index++)
        {
            chunks[index].ShouldBe(text.Substring(index * (cap - 200), cap));
        }
    }

    /// <summary>
    /// A text whose only breaks are line breaks is cut on them when they fall in the last 20% of the window: each
    /// piece then ends where a line ends — never mid-line — and the pieces still overlap.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_SplitsOnLineBreaks_WhenTheBreakIsLateInTheWindow()
    {
        const int cap = ModerationOptions.MinCharsPerChunk;
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var gate = Gate(moderator, new RecordingEvents(), metrics, maxCharsPerChunk: cap);
        const string line = "0123456789";
        var text = string.Join('\n', Enumerable.Repeat(line, 10).Select(static value => string.Concat(Enumerable.Repeat(value, 10))));
        text.Length.ShouldBe((10 * 100) + 9);

        await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                StoryTextSegments.Create(null, null, [text], null, null),
                [],
                AutopilotGateProfile.Defaults));

        var chunks = moderator.Checks[0];
        chunks.ShouldAllBe(chunk => chunk.Length <= cap);
        chunks.Count.ShouldBe(4, $"one piece per ~400 characters, not one per line; lengths=[{Lengths(chunks)}]");

        // Every line is identical, so an occurrence search would find the first equal run instead of the piece's
        // own offset: the offsets are walked arithmetically, and the cut positions are checked against the text.
        var offset = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            text.Substring(offset, chunks[index].Length).ShouldBe(chunks[index], $"piece {index} at {offset}");
            var end = offset + chunks[index].Length;
            if (index < chunks.Count - 1)
            {
                text[end].ShouldBe('\n', $"piece {index} must end where a line ends, never mid-line");
                offset += chunks[index].Length - 200;
            }
            else
            {
                end.ShouldBe(text.Length, "the last piece reaches the end of the text");
            }
        }
    }

    /// <summary>
    /// A combining mark may never begin a piece: the base letter it belongs to stays in the piece before it, which
    /// therefore ends one character short of the cap instead of splitting the grapheme cluster.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_CombiningSequenceAtTheCut_KeepsTheClusterWhole()
    {
        const int cap = ModerationOptions.MinCharsPerChunk;
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var gate = Gate(moderator, new RecordingEvents(), metrics, maxCharsPerChunk: cap);
        var text = new string('a', cap) + "\u0301" + new string('b', 200);

        await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                StoryTextSegments.Create(null, null, [text], null, null),
                [],
                AutopilotGateProfile.Defaults));

        var chunks = moderator.Checks[0];
        chunks.Count.ShouldBe(2, $"lengths=[{Lengths(chunks)}]");
        chunks[0].Length.ShouldBe(cap - 1, "the cut steps back over the base letter of the combining mark");
        chunks[0].ShouldBe(new string('a', cap - 1));
        text[chunks[0].Length].ShouldBe('a', "the cut stops before the base letter of the mark");
        text[chunks[0].Length + 1].ShouldBe('\u0301', "the mark stays behind its base, never alone");
        chunks[1].ShouldContain("a\u0301", Case.Sensitive, "the cluster stays whole in one piece");
        chunks.ShouldAllBe(chunk => chunk.Length > 0 && !IsCombiningMark(chunk[0]));
    }

    private static bool IsCombiningMark(char value) =>
        CharUnicodeInfo.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// An astral character followed by a variation selector is one cluster of three characters: the cut must step
    /// back over the mark AND then over the surrogate pair, never leaving a lone high surrogate at the end of a
    /// piece (the mark walk alone would undo the pair check).
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_AstralClusterAtTheCut_KeepsTheSurrogatePairWhole()
    {
        const int cap = ModerationOptions.MinCharsPerChunk;
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Clean();
        var gate = Gate(moderator, new RecordingEvents(), metrics, maxCharsPerChunk: cap);
        var text = new string('a', cap - 2) + "\U0001F600\uFE0F" + new string('b', 200);

        await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                StoryTextSegments.Create(null, null, [text], null, null),
                [],
                AutopilotGateProfile.Defaults));

        var chunks = moderator.Checks[0];
        chunks.Count.ShouldBe(2, $"lengths=[{Lengths(chunks)}]");
        chunks[0].Length.ShouldBe(cap - 2, "the cut steps back over the mark and then over the whole pair");
        chunks[0].ShouldBe(new string('a', cap - 2));
        char.IsHighSurrogate(text[chunks[0].Length]).ShouldBeTrue("the pair starts at the cut, whole");
        char.IsLowSurrogate(text[chunks[0].Length + 1]).ShouldBeTrue("the pair stays together");
        chunks[1].ShouldContain("\U0001F600\uFE0F", Case.Sensitive, "the cluster stays whole in one piece");
        chunks.ShouldAllBe(
            chunk => !char.IsHighSurrogate(chunk[^1]) && !char.IsLowSurrogate(chunk[0]) && !IsCombiningMark(chunk[0]));
    }

    private static string Lengths(IReadOnlyList<string> chunks) =>
        string.Join(",", chunks.Select(chunk => chunk.Length));

    /// <summary>
    /// A failing packed chunk is narrowed segment by segment, so the owner is told which paragraph to re-read
    /// rather than the three paragraphs that shared a moderator text (SEC-02): one row, one excerpt, one address.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_PackedChunk_NarrowsTheFailureToItsParagraph()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator()
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Clean()
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Clean();
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics, maxCharsPerChunk: ModerationOptions.MinCharsPerChunk);
        var first = "First paragraph. " + new string('a', 200);
        var second = "Second paragraph. " + new string('b', 200);
        var third = "Third paragraph. " + new string('c', 200);

        var verdict = await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                StoryTextSegments.Create(null, null, [first, second, third], null, null),
                [],
                AutopilotGateProfile.Defaults));

        verdict.Flagged.ShouldBeTrue();
        verdict.FlaggedSegments.ShouldBe(["p:1"], "the packed text is narrowed to the paragraph that failed");
        moderator.Checks.Count.ShouldBe(5, "one union call, one per chunk, then the packed chunk's segments");
        events.Items.ShouldHaveSingleItem().OwnText.ShouldBe(second);
    }

    /// <summary>
    /// When no segment of a packed chunk fails on its own, the combination did: the whole chunk is the failing unit
    /// — every address it holds is named and its opening characters are the excerpt, so the owner still has the
    /// text to look at.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_PackedChunkWithNoIndividualFailure_NamesEverySegment()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator()
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Clean()
            .Clean()
            .Clean();
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics, maxCharsPerChunk: ModerationOptions.MinCharsPerChunk);
        var first = "First paragraph. " + new string('a', 200);
        var second = "Second paragraph. " + new string('b', 200);
        var third = "Third paragraph. " + new string('c', 200);

        var verdict = await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                StoryTextSegments.Create(null, null, [first, second, third], null, null),
                [],
                AutopilotGateProfile.Defaults));

        verdict.Flagged.ShouldBeTrue();
        verdict.FlaggedSegments.ShouldBe(["p:0", "p:1"], "the packed text is the failing unit");
        events.Items.ShouldHaveSingleItem().OwnText.ShouldBe($"{first}\n\n{second}");
    }

    /// <summary>
    /// Overlapping pieces of one oversized segment are the same evidence: the gate writes one row per
    /// (segment set, categories) pair, not one per piece, so a long failing paragraph cannot flood the table with
    /// near-duplicate excerpts (SEC-02/P2).
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_OverlappingPieces_WriteOneEvidenceRow()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator()
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate")
            .Verdict(flagged: true, Scores(("hate", 0.9)), "hate");
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics, maxCharsPerChunk: ModerationOptions.MinCharsPerChunk);

        var verdict = await CheckAsync(
            gate,
            new AutopilotContentGateRequest(
                UserId,
                EpisodeId,
                VersionId,
                StoryTextSegments.Create(null, null, [new string('q', 1350)], null, null),
                [],
                AutopilotGateProfile.Defaults));

        verdict.Flagged.ShouldBeTrue();
        verdict.FlaggedSegments.ShouldBe(["p:0"]);
        moderator.Checks.Count.ShouldBe(5, "one union call and one per piece");
        events.Items.ShouldHaveSingleItem("four pieces of one segment are one evidence row");
    }

    /// <summary>
    /// A provider flag whose category carries no score cannot be compared against the profile's ceiling, so it is
    /// an unverifiable flag and fails closed (SEC-02/SEC-04): a missing <c>category_scores</c> entry must never read
    /// as "within the ceiling".
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_FlaggedCategoryWithoutScore_FailsClosed()
    {
        using var metrics = new ContentGateMetrics();
        var moderator = new ScriptedModerator().Verdict(flagged: true, Scores(), "violence");
        var events = new RecordingEvents();
        var gate = Gate(moderator, events, metrics);

        var verdict = await CheckAsync(gate, SingleTextRequest());

        verdict.Flagged.ShouldBeTrue("a flag with no score is unverifiable, not a pass");
        verdict.Categories.ShouldBe(["violence"]);
        verdict.FlaggedSegments.ShouldBe(["title"]);
        events.Items.ShouldHaveSingleItem().Categories.ShouldBe(["violence"]);
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
                StoryTextSegments.Create(null, null, [first, second], null, null),
                [],
                AutopilotGateProfile.Defaults));

        verdict.Flagged.ShouldBeTrue();
        verdict.FlaggedSegments.ShouldBe(["p:0", "p:1"]);
        moderator.Checks.Count.ShouldBe(3, "one union check, then one per chunk to attribute");
        events.Items.Count.ShouldBe(2, "one row per failing unit: each paragraph of the packed text narrowed on its own");
        events.Items[0].OwnText.ShouldBe(first);
        events.Items[1].OwnText.ShouldBe(second);
        events.Items.ShouldAllBe(item => item.EpisodeId == EpisodeId && item.UserId == UserId);
        events.Items.ShouldAllBe(item => item.Categories.SequenceEqual(new[] { "hate" }));
    }

    /// <summary>
    /// The union verdict is one answer over every text, so the gate narrows it before writing evidence: the packed
    /// narration text is asked segment by segment and only the segment that fails on its own is named, which is what
    /// the owner reads in the run log (D22).
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_ReCheckAttributesOnlyTheFailingSegment()
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
    public async Task AutopilotModerationGate_UnavailableDuringNarrowing_KeepsFailClosed()
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
        events.Items.Count.ShouldBe(1, "the units attributed before the outage keep their row");
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

    /// <summary>
    /// T3 reads the counter per failing unit: a packed text that narrows to two flagged paragraphs counts both of
    /// them, and the category that only one of them breached counts once.
    /// </summary>
    [Fact]
    public async Task AutopilotModerationGate_CountsEachFlaggedCategoryPerFailingUnit()
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
                StoryTextSegments.Create(null, null, [first, second], null, null),
                [],
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
            StoryTextSegments.Create(title, hook, paragraphs ?? ["Para one.", "Para two."], ending, meta),
            scenes ?? ["scene text"],
            AutopilotGateProfile.Defaults);

    private static AutopilotContentGateRequest SingleTextRequest() =>
        new(
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
