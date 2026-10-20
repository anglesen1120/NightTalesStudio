using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The content gate of the story gate (02.8.2-T2): moderates the final narration, the metadata and every expanded
/// scene prompt in one <c>omni-moderation-latest</c> call, decides check 7 against the profile's per-category
/// thresholds, and writes one <c>moderation_events</c> row per failing chunk.
/// </summary>
/// <remarks>
/// <para>
/// <b>One chunk per D22 segment.</b> Every segment of <see cref="AutopilotContentGateRequest.Segments"/> and every
/// scene prompt is its own text, chunked to the moderator cap
/// (<see cref="ModerationOptions.MaxCharsPerChunk"/>) on a paragraph or word boundary — never mid-word, mid
/// surrogate pair or mid grapheme — with the port's 200-character overlap between adjacent pieces, so a phrase
/// over a cut stays whole in one of them (SEC-03), and every chunk keeps its segment id. The union verdict
/// therefore still names the segment (<c>title</c>, <c>hook</c>, <c>p:&lt;i&gt;</c>, <c>ending</c>, <c>meta</c>, <c>scene:&lt;i&gt;</c>)
/// that made the story fail, which is what the owner reads in the run log (D22).
/// </para>
/// <para>
/// <b>The thresholds decide, not the provider's flag.</b> A chunk fails when a flagged category is blocked by the
/// profile, has no ceiling at all, or scores above its ceiling, and also when a category's score breaches its
/// ceiling without the provider flagging it — that last rule is what makes the thresholds mean anything (plan
/// §3.3, Acceptance 2). A provider flag under its ceiling (violence 0.55 against 0.6) is a pass, because horror
/// fiction must remain possible.
/// </para>
/// <para>
/// <b>Attribution only on a failure.</b> The moderator answers one union verdict; when it fails and there is more
/// than one chunk, the gate asks again chunk by chunk (same stage) to know which segments to name and to write the
/// evidence rows. An outage during that second pass keeps the fail-closed verdict and writes the rows it could
/// attribute.
/// </para>
/// <para>
/// <b>Fail closed, never silent.</b> A moderator outage is a flagged verdict with
/// <c>moderator_unavailable</c> and no evidence row (nothing was classified); a request with nothing to moderate
/// is a flagged verdict with <c>field_absent</c> — nothing moderated is not a pass.
/// </para>
/// <para>
/// <b>The caller owns the moderation scope.</b> The moderator records its zero-cost <c>usage</c> row from
/// <see cref="ModerationScope"/>, exactly as the story and fact-sheet pipelines do; the gate takes no run context
/// (its request carries ids only), so the evaluator that holds the run opens the scope around this call.
/// </para>
/// <para>
/// <b>Nothing here is logged but ids and counts.</b> Categories are provider vocabulary, the excerpts stay in
/// <c>moderation_events</c>, and no narration, title, description, tag or scene prompt reaches a log line (D19).
/// </para>
/// </remarks>
/// <param name="moderator">The D12 moderation port (free; it meters its own usage row).</param>
/// <param name="moderationEvents">The one writer of <c>moderation_events</c>: one row per failing chunk.</param>
/// <param name="options">The bound <c>OpenAi:Moderation</c> section: the chunk cap this gate pre-splits to.</param>
/// <param name="metrics">The per-category flag counter of T3's calibration.</param>
/// <param name="log">The outcome log: ids and counts only.</param>
public sealed partial class AutopilotModerationGate(
    IContentModerator moderator,
    IModerationEventWriter moderationEvents,
    ModerationOptions options,
    ContentGateMetrics metrics,
    ILogger<AutopilotModerationGate> log) : IAutopilotContentGate
{
    /// <summary>
    /// The evidence write's own deadline, independent of the caller's token: a cancelled job still keeps its
    /// <c>moderation_events</c> rows, exactly as the idea pre-check's writer does (the writer swallows a failure
    /// beyond it).
    /// </summary>
    private static readonly TimeSpan EvidenceWriteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far every piece after the first steps back into the previous one, so a phrase over a cut is whole in one
    /// piece. The same number the moderator port applies to a text it has to split itself (SEC-03); the port never
    /// re-splits a piece this gate produced, so the guarantee has to hold here.
    /// </summary>
    private const int ChunkOverlap = 200;

    /// <inheritdoc />
    public async ValueTask<ErrorOr<ContentGateVerdict>> CheckAsync(
        AutopilotContentGateRequest req,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ct.ThrowIfCancellationRequested();

        var chunks = BuildChunks(req, options.MaxCharsPerChunk);
        if (chunks.Count == 0)
        {
            LogNothingToModerate(req.EpisodeId, req.StoryVersionId);

            return new ContentGateVerdict(
                true,
                [],
                new Dictionary<string, double>(StringComparer.Ordinal),
                GateDetailCodes.FieldAbsent,
                []);
        }

        ModerationResult union;
        try
        {
            union = await moderator
                .CheckAsync([.. chunks.Select(chunk => chunk.Text)], ModerationStage.AutopilotGate, ct)
                .ConfigureAwait(false);
        }
        catch (ModerationUnavailableException)
        {
            // Fail closed (plan §7): nothing was classified, so there is no evidence row to write.
            LogUnavailable(req.EpisodeId, req.StoryVersionId, chunks.Count);

            return new ContentGateVerdict(
                true,
                [],
                new Dictionary<string, double>(StringComparer.Ordinal),
                GateDetailCodes.ModeratorUnavailable,
                []);
        }

        if (!Fails(union, req.Profile.Moderation))
        {
            LogChecked(req.EpisodeId, req.StoryVersionId, chunks.Count, flagged: false, categoryCount: 0);

            return new ContentGateVerdict(false, [], union.Scores, null, []);
        }

        var failing = await AttributeAsync(req, chunks, union, ct).ConfigureAwait(false);

        var segments = new List<string>();
        foreach (var (chunk, categories) in failing)
        {
            if (!segments.Contains(chunk.SegmentId, StringComparer.Ordinal))
            {
                segments.Add(chunk.SegmentId);
            }

            // One count per flagged category of one failing chunk (T3 reads this distribution per category).
            foreach (var category in categories)
            {
                if (string.IsNullOrWhiteSpace(category))
                {
                    // A provider that answered a blank category name must not turn a verdict into an exception.
                    continue;
                }

                metrics.Flagged(category);
            }
        }

        foreach (var (chunk, categories) in failing)
        {
            await WriteEvidenceAsync(req, chunk, categories).ConfigureAwait(false);
        }

        var verdictCategories = EffectiveCategories(union, req.Profile.Moderation);
        LogChecked(req.EpisodeId, req.StoryVersionId, chunks.Count, flagged: true, categoryCount: verdictCategories.Count);
        LogEvidenceWritten(req.EpisodeId, req.StoryVersionId, segments.Count, failing.Count);

        return new ContentGateVerdict(true, verdictCategories, union.Scores, null, segments);
    }

    /// <summary>
    /// Which chunks made the union verdict fail: when there is one chunk it is that chunk's verdict, otherwise the
    /// gate asks chunk by chunk so the evidence names the segment (plan step 5). A re-check that cannot complete
    /// stops the attribution, not the verdict.
    /// </summary>
    private async ValueTask<List<(Chunk Chunk, IReadOnlyList<string> Categories)>> AttributeAsync(
        AutopilotContentGateRequest req,
        IReadOnlyList<Chunk> chunks,
        ModerationResult union,
        CancellationToken ct)
    {
        var failing = new List<(Chunk Chunk, IReadOnlyList<string> Categories)>();

        if (chunks.Count == 1)
        {
            failing.Add((chunks[0], EffectiveCategories(union, req.Profile.Moderation)));

            return failing;
        }

        foreach (var chunk in chunks)
        {
            ModerationResult chunkVerdict;
            try
            {
                chunkVerdict = await moderator
                    .CheckAsync([chunk.Text], ModerationStage.AutopilotGate, ct)
                    .ConfigureAwait(false);
            }
            catch (ModerationUnavailableException)
            {
                LogRecheckUnavailable(req.EpisodeId, req.StoryVersionId, failing.Count);

                return failing;
            }

            if (Fails(chunkVerdict, req.Profile.Moderation))
            {
                failing.Add((chunk, EffectiveCategories(chunkVerdict, req.Profile.Moderation)));
            }
        }

        return failing;
    }

    /// <summary>
    /// Whether a verdict fails the profile's thresholds (02.8.2-T2 decision 5, all fail closed): a blocked or
    /// unceilinged flagged category, a flagged category above its ceiling, any category above its ceiling, or a
    /// flag with no category at all.
    /// </summary>
    private static bool Fails(ModerationResult verdict, ModerationThresholds thresholds)
    {
        if (verdict.Flagged && verdict.Categories.Count == 0)
        {
            return true;
        }

        foreach (var category in verdict.Categories)
        {
            if (thresholds.BlockedCategories.Contains(category, StringComparer.Ordinal))
            {
                return true;
            }

            if (!thresholds.MaxScore.TryGetValue(category, out var ceiling))
            {
                return true;
            }

            if (verdict.Scores.TryGetValue(category, out var score) && score > ceiling)
            {
                return true;
            }
        }

        foreach (var (category, score) in verdict.Scores)
        {
            if (thresholds.MaxScore.TryGetValue(category, out var ceiling) && score > ceiling)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The categories a verdict failed on: the provider's flagged ones plus every category whose score breached
    /// its ceiling without being flagged, so the evidence names the reason even when the provider disagreed.
    /// </summary>
    private static List<string> EffectiveCategories(ModerationResult verdict, ModerationThresholds thresholds)
    {
        var categories = new List<string>();

        foreach (var category in verdict.Categories)
        {
            if (!categories.Contains(category, StringComparer.Ordinal))
            {
                categories.Add(category);
            }
        }

        foreach (var (category, score) in verdict.Scores)
        {
            if (thresholds.MaxScore.TryGetValue(category, out var ceiling)
                && score > ceiling
                && !categories.Contains(category, StringComparer.Ordinal))
            {
                categories.Add(category);
            }
        }

        return categories;
    }

    /// <summary>
    /// The chunked texts of the request, in content order: every non-blank D22 segment and then every non-blank
    /// scene prompt, each keeping its segment id. Blank texts are skipped; nothing else is.
    /// </summary>
    private static List<Chunk> BuildChunks(AutopilotContentGateRequest req, int maxChars)
    {
        var chunks = new List<Chunk>();

        foreach (var segment in req.Segments)
        {
            AddChunks(chunks, segment.Id, segment.Text, maxChars);
        }

        for (var index = 0; index < req.ExpandedScenePrompts.Count; index++)
        {
            // The index is the keyword's position, so a blank prompt does not renumber the scenes after it.
            AddChunks(chunks, SceneSegmentId(index), req.ExpandedScenePrompts[index], maxChars);
        }

        return chunks;
    }

    /// <summary>The D22-style address of a scene prompt: <c>scene:&lt;i&gt;</c>, 0-based.</summary>
    private static string SceneSegmentId(int index) => string.Create(
        CultureInfo.InvariantCulture,
        $"scene:{index}");

    private static void AddChunks(List<Chunk> chunks, string segmentId, string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var piece in Split(text, maxChars))
        {
            chunks.Add(new Chunk(segmentId, piece));
        }
    }

    /// <summary>
    /// Splits one text to at most <paramref name="maxChars"/> characters per piece, on a paragraph break first,
    /// then a line break, then any whitespace, and only then as a hard cut that never separates a surrogate pair
    /// or a grapheme cluster (a combining sequence stays whole). Adjacent pieces overlap by
    /// <see cref="ChunkOverlap"/> characters (never more than <c>maxChars - 1</c>), so a phrase over a cut is whole
    /// in one piece — the same guarantee the moderator port gives when it has to split a text itself (SEC-03),
    /// which it never does with a piece this gate produced.
    /// </summary>
    private static List<string> Split(string text, int maxChars)
    {
        var cap = Math.Max(1, maxChars);
        var overlap = Math.Min(ChunkOverlap, cap - 1);
        var pieces = new List<string>();
        if (text.Length <= cap)
        {
            pieces.Add(text.Trim());

            return pieces;
        }

        var start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            if (start >= text.Length)
            {
                break;
            }

            if (text.Length - start <= cap)
            {
                var tail = text[start..].TrimEnd();
                if (tail.Length > 0)
                {
                    pieces.Add(tail);
                }

                break;
            }

            var limit = start + cap;
            var cut = Boundary(text, start, limit);
            var piece = text[start..cut].TrimEnd();
            if (piece.Length > 0)
            {
                pieces.Add(piece);
            }

            // Step back by the overlap so the next piece repeats this one's tail; never below the current start,
            // so the loop always progresses, and never onto the low half of a surrogate pair.
            var next = Math.Max(start + 1, cut - overlap);
            if (next > start + 1 && char.IsLowSurrogate(text[next]))
            {
                next--;
            }

            start = next;
        }

        if (pieces.Count == 0)
        {
            pieces.Add(text.Trim());
        }

        return pieces;
    }

    /// <summary>The cut index of the window [<paramref name="start"/>, <paramref name="limit"/>), preferring a
    /// boundary a reader would recognise over the raw cap.</summary>
    private static int Boundary(string text, int start, int limit)
    {
        var window = text.AsSpan(start, limit - start);

        var paragraph = window.LastIndexOf("\n\n".AsSpan(), StringComparison.Ordinal);
        if (paragraph > 0)
        {
            return start + paragraph;
        }

        var line = window.LastIndexOf('\n');
        if (line > 0)
        {
            return start + line;
        }

        for (var index = window.Length - 1; index > 0; index--)
        {
            if (char.IsWhiteSpace(window[index]))
            {
                return start + index;
            }
        }

        return GraphemeSafeCut(text, start, limit);
    }

    /// <summary>
    /// The last text-element boundary at or before <paramref name="limit"/>: a cut can never leave a high
    /// surrogate without its low half (the insert would fail) or separate a combining mark from its base letter.
    /// A single cluster longer than the cap is cut at the cap — the only forced cut in this class.
    /// </summary>
    private static int GraphemeSafeCut(string text, int start, int limit)
    {
        var target = Math.Min(limit, text.Length);
        var end = start;
        var enumerator = StringInfo.GetTextElementEnumerator(text, start);

        while (enumerator.MoveNext())
        {
            var elementStart = enumerator.ElementIndex;
            var elementEnd = elementStart + enumerator.GetTextElement().Length;
            if (elementEnd > target)
            {
                break;
            }

            end = elementEnd;
        }

        return end > start ? end : target;
    }

    /// <summary>
    /// Writes the one <c>moderation_events</c> row of a failing chunk: our own generated text as the excerpt (the
    /// domain keeps its first 300 characters), on the writer's own deadline so a cancelled job keeps its evidence.
    /// </summary>
    private async ValueTask WriteEvidenceAsync(
        AutopilotContentGateRequest req,
        Chunk chunk,
        IReadOnlyList<string> categories)
    {
        using var terminal = new CancellationTokenSource(EvidenceWriteTimeout);

        await moderationEvents
            .WriteAsync(
                req.UserId,
                req.EpisodeId,
                ModerationSource.OpenaiText,
                ModerationStage.AutopilotGate,
                categories,
                TextSafety.Clean(chunk.Text),
                terminal.Token)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 6503,
        Level = LogLevel.Warning,
        Message = "Content gate stage autopilot_gate has nothing to moderate for episode {EpisodeId} version {StoryVersionId}: fail closed")]
    private partial void LogNothingToModerate(Guid episodeId, Guid storyVersionId);

    [LoggerMessage(
        EventId = 6504,
        Level = LogLevel.Error,
        Message = "Content gate stage autopilot_gate unavailable for episode {EpisodeId} version {StoryVersionId} chunks {ChunkCount}: fail closed")]
    private partial void LogUnavailable(Guid episodeId, Guid storyVersionId, int chunkCount);

    [LoggerMessage(
        EventId = 6505,
        Level = LogLevel.Information,
        Message = "Content gate stage autopilot_gate checked episode {EpisodeId} version {StoryVersionId} chunks {ChunkCount} flagged {Flagged} categories {CategoryCount}")]
    private partial void LogChecked(Guid episodeId, Guid storyVersionId, int chunkCount, bool flagged, int categoryCount);

    [LoggerMessage(
        EventId = 6506,
        Level = LogLevel.Warning,
        Message = "Content gate re-check unavailable for episode {EpisodeId} version {StoryVersionId} after {AttributedChunks} attributed chunks: keeping the fail-closed verdict")]
    private partial void LogRecheckUnavailable(Guid episodeId, Guid storyVersionId, int attributedChunks);

    [LoggerMessage(
        EventId = 6508,
        Level = LogLevel.Information,
        Message = "Content gate wrote moderation evidence for episode {EpisodeId} version {StoryVersionId}: segments {SegmentCount} events {EventCount}")]
    private partial void LogEvidenceWritten(Guid episodeId, Guid storyVersionId, int segmentCount, int eventCount);

    private sealed record Chunk(string SegmentId, string Text);
}
