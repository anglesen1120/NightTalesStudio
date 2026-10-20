using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The content gate of the story gate (02.8.2-T2): moderates the final narration, the metadata and every expanded
/// scene prompt in one <c>omni-moderation-latest</c> call, decides check 7 against the profile's per-category
/// thresholds, and writes one <c>moderation_events</c> row per failing attribution unit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Packed to the cap, addressed by D22 segment.</b> The narration (title, hook, <c>p:&lt;i&gt;</c>, ending, in
/// story order) is packed greedily into as few texts as the moderator cap
/// (<see cref="ModerationOptions.MaxCharsPerChunk"/>) allows, joined by a blank line, so a short story is one text
/// rather than five and no request is spent on a two-line paragraph. The metadata (<c>meta</c>: description and
/// tags) and every expanded scene prompt stay their own text — different kinds of content, and a shared excerpt
/// would mislead the owner. A text that still exceeds the cap is split on a paragraph break, then a line break,
/// then whitespace, and each of those only in the last 20% of the window, so the next piece always starts at
/// least <c>0.8 * cap - 200</c> characters later and the split can never crawl forward a character at a time
/// (D2); only then a hard cut, which never separates a surrogate pair or a combining sequence. Adjacent pieces
/// overlap by 200 characters, so a phrase over a cut stays whole in one of them (SEC-03). Every chunk records the
/// distinct D22 segment ids it was built from (<c>title</c>, <c>hook</c>, <c>p:&lt;i&gt;</c>, <c>ending</c>,
/// <c>meta</c>, <c>scene:&lt;i&gt;</c>), which is what a failure is attributed to (D22).
/// </para>
/// <para>
/// <b>The thresholds decide, not the provider's flag.</b> A chunk fails when a flagged category is blocked by the
/// profile, has no ceiling at all, has no score to compare against that ceiling, or scores above it, and also when
/// a category's score breaches its ceiling without the provider flagging it — that last rule is what makes the
/// thresholds mean anything (plan §3.3, Acceptance 2). A provider flag under its ceiling (violence 0.55 against
/// 0.6) is a pass, because horror fiction must remain possible.
/// </para>
/// <para>
/// <b>Attribution only on a failure, and only as far as it narrows the blame.</b> The moderator answers one union
/// verdict; when it fails and there is more than one chunk, the gate asks again chunk by chunk (same stage) to
/// know which texts to name and to write the evidence rows. A failing packed chunk that holds more than one
/// segment is then re-checked segment by segment — with a single chunk the union verdict is that chunk's verdict,
/// so only a packed one needs narrowing — and a flagged paragraph names <c>p:2</c> instead of the three
/// paragraphs it shared a text with. When no single segment of that chunk fails on its own, the chunk as a whole
/// is the failing unit: every segment id of it is named and the row carries the chunk's opening characters. One
/// <c>moderation_events</c> row is written per failing unit — a narrowed segment, a single-segment chunk, or a
/// packed chunk that could not be narrowed — under one shared deadline, de-duplicated by segment set and
/// categories, so overlapping pieces of one oversized segment never record the same evidence twice. An outage
/// during that second pass keeps the fail-closed verdict and the rows it could attribute.
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
/// <param name="moderationEvents">The writer of <c>moderation_events</c>: one row per failing attribution unit.</param>
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
    /// beyond it). One budget covers the whole loop of rows, not one per row, so a long failing story cannot hold
    /// the gate open for a multiple of it.
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

        var failures = await AttributeAsync(req, chunks, union, ct).ConfigureAwait(false);

        var segments = new List<string>();
        foreach (var failure in failures)
        {
            foreach (var segmentId in failure.SegmentIds)
            {
                if (!segments.Contains(segmentId, StringComparer.Ordinal))
                {
                    segments.Add(segmentId);
                }
            }

            // One count per flagged category of one failing unit (T3 reads this distribution per category).
            foreach (var category in failure.Categories)
            {
                if (string.IsNullOrWhiteSpace(category))
                {
                    // A provider that answered a blank category name must not turn a verdict into an exception.
                    continue;
                }

                metrics.Flagged(category);
            }
        }

        await WriteEvidenceAsync(req, failures).ConfigureAwait(false);

        var verdictCategories = EffectiveCategories(union, req.Profile.Moderation);
        LogChecked(req.EpisodeId, req.StoryVersionId, chunks.Count, flagged: true, categoryCount: verdictCategories.Count);
        LogEvidenceWritten(req.EpisodeId, req.StoryVersionId, segments.Count, failures.Count);

        return new ContentGateVerdict(true, verdictCategories, union.Scores, null, segments);
    }

    /// <summary>
    /// Which texts made the union verdict fail, narrowed as far as the chunks allow: with one chunk the union
    /// verdict is that chunk's verdict, so only a packed chunk needs the extra calls; with several chunks the gate
    /// asks chunk by chunk, and a failing packed chunk is then asked segment by segment (plan step 5). A re-check
    /// that cannot complete stops the attribution, never the verdict.
    /// </summary>
    private async ValueTask<List<Failure>> AttributeAsync(
        AutopilotContentGateRequest req,
        IReadOnlyList<Chunk> chunks,
        ModerationResult union,
        CancellationToken ct)
    {
        if (chunks.Count == 1)
        {
            var (narrowed, stopped) = await NarrowAsync(
                req,
                chunks[0],
                EffectiveCategories(union, req.Profile.Moderation),
                ct).ConfigureAwait(false);
            if (stopped)
            {
                LogRecheckUnavailable(req.EpisodeId, req.StoryVersionId, narrowed.Count);
            }

            return narrowed;
        }

        var failures = new List<Failure>();

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
                LogRecheckUnavailable(req.EpisodeId, req.StoryVersionId, failures.Count);

                return failures;
            }

            if (!Fails(chunkVerdict, req.Profile.Moderation))
            {
                continue;
            }

            var (narrowed, stopped) = await NarrowAsync(
                req,
                chunk,
                EffectiveCategories(chunkVerdict, req.Profile.Moderation),
                ct).ConfigureAwait(false);

            failures.AddRange(narrowed);

            if (stopped)
            {
                LogRecheckUnavailable(req.EpisodeId, req.StoryVersionId, failures.Count);

                return failures;
            }
        }

        return failures;
    }

    /// <summary>
    /// The failing units of one failing chunk: the chunk itself when it came from a single segment, otherwise the
    /// segments that fail on their own — and the whole chunk when none of them does, because the flag then belongs
    /// to the combination (a packed chunk's evidence is its opening characters and every segment id it holds).
    /// </summary>
    /// <param name="req">The request, for the outage log's ids.</param>
    /// <param name="chunk">The failing chunk.</param>
    /// <param name="categories">The categories its verdict failed on.</param>
    /// <param name="ct">Cancels the re-checks.</param>
    /// <returns>The failing units, and whether an outage stopped the narrowing.</returns>
    private async ValueTask<(List<Failure> Failures, bool Stopped)> NarrowAsync(
        AutopilotContentGateRequest req,
        Chunk chunk,
        IReadOnlyList<string> categories,
        CancellationToken ct)
    {
        if (chunk.Parts.Count <= 1)
        {
            return ([new Failure(chunk.Text, chunk.SegmentIds, categories)], false);
        }

        var failures = new List<Failure>();

        foreach (var part in chunk.Parts)
        {
            ModerationResult partVerdict;
            try
            {
                partVerdict = await moderator
                    .CheckAsync([part.Text], ModerationStage.AutopilotGate, ct)
                    .ConfigureAwait(false);
            }
            catch (ModerationUnavailableException)
            {
                return (failures, true);
            }

            if (Fails(partVerdict, req.Profile.Moderation))
            {
                failures.Add(new Failure(
                    part.Text,
                    [part.SegmentId],
                    EffectiveCategories(partVerdict, req.Profile.Moderation)));
            }
        }

        if (failures.Count > 0)
        {
            return (failures, false);
        }

        return ([new Failure(chunk.Text, chunk.SegmentIds, categories)], false);
    }

    /// <summary>
    /// Whether a verdict fails the profile's thresholds (02.8.2-T2 decision 5, all fail closed): a blocked or
    /// unceilinged flagged category, a flagged category with no score to compare against its ceiling or one above
    /// it, any category above its ceiling, or a flag with no category at all.
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

            // A flagged category with no score cannot be compared against its ceiling — the provider answered the
            // flag without the category_scores entry (SEC-02/SEC-04). That is an unverifiable flag, not a pass:
            // treating a missing score as "within the ceiling" would let a provider flag slip through untested.
            if (!verdict.Scores.TryGetValue(category, out var score) || score > ceiling)
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
    /// The moderator texts of the request, in content order: the narration (title, hook, <c>p:&lt;i&gt;</c>,
    /// ending) packed greedily into as few texts as the cap allows, the metadata and every scene prompt as their
    /// own text, and every text that still exceeds the cap split into pieces. Blank texts are skipped; nothing
    /// else is.
    /// </summary>
    /// <param name="req">The request.</param>
    /// <param name="maxChars">The moderator's cap per text, at least 1.</param>
    /// <returns>The chunks, each holding the distinct D22 segment ids it was built from.</returns>
    private static List<Chunk> BuildChunks(AutopilotContentGateRequest req, int maxChars)
    {
        var cap = Math.Max(1, maxChars);
        var chunks = new List<Chunk>();
        var packed = new List<ChunkPart>();

        foreach (var segment in req.Segments)
        {
            // The metadata (description and tags) is a different kind of content from the prose, and its excerpt
            // would mislead the owner, so it never shares a text with the narration.
            if (string.Equals(segment.Id, SegmentAddress.Meta, StringComparison.Ordinal))
            {
                Flush(chunks, packed);
                AddChunk(chunks, new ChunkPart(segment.Id, segment.Text), cap);

                continue;
            }

            Pack(chunks, packed, new ChunkPart(segment.Id, segment.Text), cap);
        }

        Flush(chunks, packed);

        for (var index = 0; index < req.ExpandedScenePrompts.Count; index++)
        {
            // The index is the keyword's position, so a blank prompt does not renumber the scenes after it, and
            // each prompt is its own text so a flagged one names its scene.
            var text = req.ExpandedScenePrompts[index];
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            AddChunk(chunks, new ChunkPart(SceneSegmentId(index), text), cap);
        }

        return chunks;
    }

    /// <summary>The D22-style address of a scene prompt: <c>scene:&lt;i&gt;</c>, 0-based.</summary>
    private static string SceneSegmentId(int index) => string.Create(
        CultureInfo.InvariantCulture,
        $"scene:{index}");

    /// <summary>
    /// Packs one narration segment into the pending text when it still fits the cap, otherwise closes the pending
    /// text and takes the segment on its own (split when it alone is over the cap).
    /// </summary>
    /// <param name="chunks">The chunks built so far.</param>
    /// <param name="packed">The segments of the text being filled.</param>
    /// <param name="part">The segment to add.</param>
    /// <param name="cap">The cap per text.</param>
    private static void Pack(List<Chunk> chunks, List<ChunkPart> packed, ChunkPart part, int cap)
    {
        var text = part.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (packed.Count > 0)
        {
            var candidate = Join([.. packed, new ChunkPart(part.SegmentId, text)]);
            if (candidate.Length > cap)
            {
                Flush(chunks, packed);
            }
        }

        if (text.Length > cap)
        {
            // A segment larger than the cap is split on its own, so no piece mixes it with its neighbour.
            Flush(chunks, packed);
            AddChunk(chunks, new ChunkPart(part.SegmentId, text), cap);

            return;
        }

        packed.Add(new ChunkPart(part.SegmentId, text));
    }

    /// <summary>Closes the text being filled, when it holds anything, and starts a new one.</summary>
    /// <param name="chunks">The chunks built so far.</param>
    /// <param name="packed">The segments of the text being filled; emptied.</param>
    private static void Flush(List<Chunk> chunks, List<ChunkPart> packed)
    {
        if (packed.Count == 0)
        {
            return;
        }

        chunks.Add(new Chunk(Join(packed), [.. packed]));
        packed.Clear();
    }

    /// <summary>The text of one chunk: its segments joined by a blank line, so no phrase reads across.</summary>
    /// <param name="parts">The segments, in story order.</param>
    /// <returns>The joined text.</returns>
    private static string Join(IReadOnlyList<ChunkPart> parts) =>
        string.Join("\n\n", parts.Select(part => part.Text));

    /// <summary>
    /// Adds one non-blank text as its own chunk, split into pieces when it exceeds the cap: each piece keeps the
    /// segment id, so an oversized paragraph still names <c>p:&lt;i&gt;</c>.
    /// </summary>
    /// <param name="chunks">The chunks built so far.</param>
    /// <param name="part">The segment, trimmed.</param>
    /// <param name="cap">The cap per text.</param>
    private static void AddChunk(List<Chunk> chunks, ChunkPart part, int cap)
    {
        var text = part.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        foreach (var piece in Split(text, cap))
        {
            chunks.Add(new Chunk(piece, [new ChunkPart(part.SegmentId, piece)]));
        }
    }

    /// <summary>
    /// Splits one text to at most <paramref name="maxChars"/> characters per piece. A piece that has to be cut ends
    /// on a paragraph break first, then a line break, then any whitespace — each of those only in the last 20% of
    /// the window, the same rule the moderator port applies when it splits a text itself — and only then on a hard
    /// cut that never separates a surrogate pair or a combining sequence. A break near the start of the window is
    /// never taken: the next piece starts <see cref="ChunkOverlap"/> characters before the cut, so a short piece
    /// would put the next start at (or behind) the current one and the split would crawl forward a character at a
    /// time (D2). That next start also snaps back off the low half of a pair or a combining mark, by at most half
    /// the piece, so no piece begins mid-cluster. Adjacent pieces therefore overlap by
    /// <see cref="ChunkOverlap"/> characters (never more than <c>maxChars - 1</c>), so a phrase over a cut is whole
    /// in one piece (SEC-03), and the last piece always reaches the end of the text.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <param name="maxChars">The cap per piece.</param>
    /// <returns>The pieces, in order; none is longer than the cap and none is blank.</returns>
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

        // Only the closing fifth of a window may end a piece (the port's rule): a cut at 80% or later of the cap
        // leaves the next start at least `0.8 * cap - overlap` characters ahead of this one.
        var tailWindow = Math.Max(0, cap / 5);

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
            var cut = Boundary(text, start, limit, tailWindow);
            var piece = text[start..cut].TrimEnd();
            if (piece.Length > 0)
            {
                pieces.Add(piece);
            }

            // Step back by the overlap so the next piece repeats this one's tail. `cut` is at least one character
            // past `start + overlap` under the window rule above; the clamps are the guard for a cap so small that
            // the overlap would swallow the whole window.
            var next = cut - overlap;
            if (next <= start)
            {
                next = start + 1;
            }

            // Never start the next piece on the low half of a surrogate pair or on a combining mark, which would
            // leave it beginning mid-cluster. The walk stops at the middle of the piece, so a text of nothing but
            // marks still advances instead of crawling.
            var nextFloor = Math.Max(start + 1, cut - ((cut - start) / 2));
            while (next > nextFloor
                && next < text.Length
                && (char.IsLowSurrogate(text[next]) || IsCombiningMark(text[next])))
            {
                next--;
            }

            if (next <= start)
            {
                next = start + 1;
            }

            start = next;
        }

        if (pieces.Count == 0)
        {
            pieces.Add(text.Trim());
        }

        return pieces;
    }

    /// <summary>
    /// The cut index of the window [<paramref name="start"/>, <paramref name="limit"/>): the last paragraph break,
    /// else the last line break, else the last whitespace, each searched only in the window's final
    /// <paramref name="tailWindow"/> characters, and otherwise the grapheme-safe cap.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The first character of the window.</param>
    /// <param name="limit">One past the window's last character.</param>
    /// <param name="tailWindow">How many characters before <paramref name="limit"/> may end the piece.</param>
    /// <returns>The cut index, greater than <paramref name="start"/>.</returns>
    private static int Boundary(string text, int start, int limit, int tailWindow)
    {
        var from = Math.Max(start, limit - tailWindow);
        if (from < limit)
        {
            var tail = text.AsSpan(from, limit - from);

            var paragraph = tail.LastIndexOf("\n\n".AsSpan(), StringComparison.Ordinal);
            if (paragraph >= 0)
            {
                return from + paragraph;
            }

            var line = tail.LastIndexOf('\n');
            if (line >= 0)
            {
                return from + line;
            }

            for (var index = tail.Length - 1; index >= 0; index--)
            {
                if (char.IsWhiteSpace(tail[index]))
                {
                    return from + index;
                }
            }
        }

        return GraphemeSafeCut(text, start, limit);
    }

    /// <summary>
    /// The last grapheme boundary at or before <paramref name="limit"/>, in the text's own index space: a cut never
    /// leaves the high half of a surrogate pair at the end of a piece, and never starts the next piece with a
    /// combining mark (the base letter it belongs to stays with it). A single cluster longer than the cap is cut at
    /// the cap — the only forced cut in this class.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The window's first character.</param>
    /// <param name="limit">One past the window's last character.</param>
    /// <returns>The cut index, greater than <paramref name="start"/>.</returns>
    private static int GraphemeSafeCut(string text, int start, int limit)
    {
        var end = Math.Min(limit, text.Length);

        // One walk that settles both rules, because a mark at `end` can point the cut back onto the low half of a
        // pair (an astral character followed by a variation selector): the pair check alone would let the mark walk
        // undo it and leave a piece ending on a lone high surrogate. Each step moves back or stops, so it terminates;
        // both bounds keep a text made only of marks or pairs advancing instead of crawling.
        var floor = Math.Max(start + 1, end - (limit - start) / 2);
        while (end > start)
        {
            if (end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]))
            {
                end--;
                continue;
            }

            if (end <= floor || end >= text.Length || !IsCombiningMark(text[end]))
            {
                break;
            }

            end--;
        }

        return end > start ? end : Math.Min(limit, text.Length);
    }

    /// <summary>Whether a character is a mark that must stay with the base character before it.</summary>
    /// <param name="value">The character.</param>
    /// <returns><see langword="true"/> for a non-spacing, spacing-combining or enclosing mark.</returns>
    private static bool IsCombiningMark(char value) =>
        CharUnicodeInfo.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// Writes one <c>moderation_events</c> row per failing attribution unit: our own generated text as the excerpt
    /// (the domain keeps its first 300 characters), all rows under one shared deadline so a cancelled job keeps its
    /// evidence, and at most one row per (segment set, categories) pair, so overlapping pieces of one oversized
    /// segment never record the same evidence twice.
    /// </summary>
    /// <param name="req">The request, for the row's ids.</param>
    /// <param name="failures">The failing units, in attribution order.</param>
    private async ValueTask WriteEvidenceAsync(
        AutopilotContentGateRequest req,
        IReadOnlyList<Failure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        using var terminal = new CancellationTokenSource(EvidenceWriteTimeout);
        var written = new HashSet<(string Segments, string Categories)>();

        foreach (var failure in failures)
        {
            if (terminal.IsCancellationRequested)
            {
                // One shared budget for the whole loop: past it the rows already written stand, and the rest wait
                // for no one — the verdict does not depend on them.
                break;
            }

            var key = (
                string.Join('\u001f', failure.SegmentIds),
                string.Join('\u001f', failure.Categories));
            if (!written.Add(key))
            {
                continue;
            }

            await moderationEvents
                .WriteAsync(
                    req.UserId,
                    req.EpisodeId,
                    ModerationSource.OpenaiText,
                    ModerationStage.AutopilotGate,
                    failure.Categories,
                    TextSafety.Clean(failure.Text),
                    terminal.Token)
                .ConfigureAwait(false);
        }
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
        Message = "Content gate re-check unavailable for episode {EpisodeId} version {StoryVersionId} after {AttributedUnits} attributed units: keeping the fail-closed verdict")]
    private partial void LogRecheckUnavailable(Guid episodeId, Guid storyVersionId, int attributedUnits);

    [LoggerMessage(
        EventId = 6508,
        Level = LogLevel.Information,
        Message = "Content gate wrote moderation evidence for episode {EpisodeId} version {StoryVersionId}: segments {SegmentCount} events {EventCount}")]
    private partial void LogEvidenceWritten(Guid episodeId, Guid storyVersionId, int segmentCount, int eventCount);

    /// <summary>One D22 segment inside a chunk: its id and the exact text the moderator was given for it.</summary>
    /// <param name="SegmentId">The D22 address (<c>title</c>, <c>hook</c>, <c>p:&lt;i&gt;</c>, <c>ending</c>, <c>meta</c>) or <c>scene:&lt;i&gt;</c>.</param>
    /// <param name="Text">That segment's text as this chunk carries it (the whole segment, or one piece of it).</param>
    private sealed record ChunkPart(string SegmentId, string Text);

    /// <summary>One text sent to the moderator, with the D22 segments it was packed from.</summary>
    /// <param name="Text">The text itself, at most the cap.</param>
    /// <param name="Parts">The segments it holds, in story order; never empty.</param>
    private sealed record Chunk(string Text, IReadOnlyList<ChunkPart> Parts)
    {
        /// <summary>The distinct D22 ids of the segments this chunk holds, in story order.</summary>
        public IReadOnlyList<string> SegmentIds
        {
            get
            {
                var ids = new List<string>(Parts.Count);
                foreach (var part in Parts)
                {
                    if (!ids.Contains(part.SegmentId, StringComparer.Ordinal))
                    {
                        ids.Add(part.SegmentId);
                    }
                }

                return ids;
            }
        }
    }

    /// <summary>
    /// One failing attribution unit and therefore one evidence row: a narrowed segment, a single-segment chunk, or
    /// a packed chunk whose segments could not be blamed individually.
    /// </summary>
    /// <param name="Text">The text whose opening characters become the row's excerpt.</param>
    /// <param name="SegmentIds">The D22 ids the unit names.</param>
    /// <param name="Categories">The categories its verdict failed on.</param>
    private sealed record Failure(string Text, IReadOnlyList<string> SegmentIds, IReadOnlyList<string> Categories);
}
