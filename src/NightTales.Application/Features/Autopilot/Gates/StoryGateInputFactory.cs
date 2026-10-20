using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Moderation;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Quality;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Usage;
using GateClaim = NightTales.Domain.Stories.Gates.RealWorldClaim;
using SimilarityText = NightTales.Domain.Stories.Similarity.StoryText;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// Reads the stored data of one story version — its <c>story_versions</c> row, its <c>audit_report</c>
/// (<c>audit.v1</c>, D22) and the series it belongs to — into everything the story gate evaluates (02.8.2 plan
/// §3.1, task step 3). It is the only place that knows the stored key names, so <see cref="StoryGateRules"/>
/// stays pure and a renamed key degrades to "owner review", never to a wrong pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never throws on stored data.</b> A null, truncated or foreign <c>audit_report</c>, a missing
/// <c>model_review</c>, an unreadable <c>content</c> document and a malformed issue entry all have a defined
/// answer here: the field reads as absent, and the Domain check that needs it fails closed with
/// <see cref="GateDetailCodes.FieldAbsent"/> (or its own code). Nothing is made clean by omission: an absent
/// <c>local</c> block, an absent issues array, an absent <c>real_world_claims</c> member and a half-written
/// <c>revision.review_after</c> are all <em>unreadable</em>, so their checks fail (F4/F6/F14). The one exception
/// is the D4 length band, which cannot be absent (check 11 has no closed value) and is answered with an
/// <see cref="ErrorOr"/> error instead of a band nobody calibrated.
/// </para>
/// <para>
/// <b>Review selection (02.6.2-T1, D22).</b> When <c>revision.applied</c> is true the current state is review #2,
/// so the scores and the issues come from <c>revision.review_after</c>; otherwise they come from
/// <c>model_review</c> — the same rule as <c>StoryReadProjection.Review</c>, and an unreadable <c>review_after</c>
/// is unknown rather than a fallback to the superseded review. <c>local.issues</c> is always unioned in, because a
/// local validator issue is not part of either review.
/// </para>
/// <para>
/// <b>The claims are unioned across both reviews (02.8.2-T2).</b> <c>real_world_claims[]</c> is not a score of the
/// text but a statement about it, and the revision rewrote the text those claims were written about, so check 8
/// reads the claims of <c>model_review</c> <em>and</em> of a complete <c>revision.review_after</c>: an extra owner
/// review is a cost, a missed claim is a hole (STATUS <c>S67-1</c>). A block whose member is absent or is not an
/// array makes the whole answer unknown — the fail-closed rule of T1 — and the claims themselves are reduced to
/// their D22 addresses, so the review model's wording never leaves <c>audit_report</c>.
/// </para>
/// <para>
/// <b>No story text leaves here.</b> The reading carries ids, codes, numbers, the D22 segment addresses of the
/// real-world claims and the parsed content document the caller still has to check (the content gate and the
/// detector both read it); nothing is copied into a gate verdict (D19/D22).
/// </para>
/// </remarks>
/// <param name="prices">The active price version, the only source of the D4 band check 11 compares against.</param>
public sealed class StoryGateInputFactory(IPriceCatalog prices)
{
    /// <summary>
    /// How many past episodes of the series check 10 scores against. The plan fixes the bound
    /// (<c>GetPastEpisodesAsync(seriesId, episodeId, 50, ct)</c>) and <c>similarity.corpus_max</c> records it, so
    /// one constant serves both.
    /// </summary>
    public const int PastEpisodeCorpusMax = 50;

    /// <summary>The D22 key of the audit's local validator issues.</summary>
    private const string LocalMember = "local";

    /// <summary>The D22 key of the draft-time moderation block (check 6).</summary>
    private const string ModerationMember = "moderation";

    /// <summary>The D22 key of the real-world claims of the model review (check 8, 02.8.2-T1 decision 1).</summary>
    private const string RealWorldClaimsMember = "real_world_claims";

    /// <summary>The D22 key of the single revision pass.</summary>
    private const string RevisionMember = "revision";

    /// <summary>The D22 key of review #2 inside <c>revision</c>.</summary>
    private const string ReviewAfterMember = "review_after";

    /// <summary>The key names of the three D22 blocks this factory reads, so a rename is one edit.</summary>
    private const string ScoresMember = "scores";

    /// <summary><c>local.issues</c> / <c>issues</c>.</summary>
    private const string IssuesMember = "issues";

    /// <summary><c>audit_report.model_review</c> (checks 1–3 and 8): the review the revision pass may supersede.</summary>
    private const string ModelReviewMember = "model_review";

    /// <summary><c>model_review.stale</c> (check 1).</summary>
    private const string StaleMember = "stale";

    /// <summary><c>revision.applied</c> (check 5).</summary>
    private const string AppliedMember = "applied";

    /// <summary><c>moderation.flagged</c> (check 6).</summary>
    private const string FlaggedMember = "flagged";

    /// <summary>An issue's <c>severity</c>.</summary>
    private const string SeverityMember = "severity";

    /// <summary>An issue's <c>kind</c>.</summary>
    private const string KindMember = "kind";

    /// <summary>An issue's <c>rule</c>.</summary>
    private const string RuleMember = "rule";

    /// <summary>One claim's D22 <c>segment</c> address.</summary>
    private const string SegmentMember = "segment";

    /// <summary>
    /// The series key the owner's gate profile is named by (plan §3.1, §3.5:
    /// <c>series.autopilot_settings.gate_thresholds_ref</c>). The jsonb document belongs to 02.8.3-T1 and has no
    /// typed Domain member, so the ref is read defensively here and an absent or unreadable one is
    /// <see langword="null"/> — which resolves to the <c>default</c> profile, never to no gate.
    /// </summary>
    private const string GateThresholdsRefMember = "gate_thresholds_ref";

    /// <summary>The most characters of a claim's D22 segment address this factory carries.</summary>
    private const int MaxSegmentChars = 64;

    /// <summary>
    /// Reads one story version into the gate's own facts.
    /// </summary>
    /// <param name="row">The stored row values; see <see cref="StoryGateRow"/>.</param>
    /// <param name="approvedFactSheetId">
    /// The series' currently approved fact sheet id, or <see langword="null"/> when the episode has none
    /// (check 14; a null on either side fails it).
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// The reading, or an error when the D4 band cannot be resolved: the active price version is missing, or it
    /// has no cell for the episode's tier × language. Both are configuration failures, and check 11 has no
    /// closed value, so the gate is not evaluated with a band nobody calibrated.
    /// </returns>
    public async ValueTask<ErrorOr<StoryGateReading>> ReadAsync(
        StoryGateRow row,
        Guid? approvedFactSheetId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(row.Keywords);

        var active = await prices.GetActiveAsync(row.CreatedAt, ct).ConfigureAwait(false);
        if (active.IsError)
        {
            return active.Errors;
        }

        LengthTarget target;
        try
        {
            target = LengthTargetResolver.Resolve(row.Tier, row.Language, active.Value.ParseLimits());
        }
        catch (KeyNotFoundException)
        {
            // The one throwing port on this path (02 README D4/D21): a band nobody seeded is not a band, so the
            // caller is answered instead of evaluating check 11 against a made-up range.
            return Error.Failure(
                ErrorCodes.InternalError,
                FormattableString.Invariant(
                    $"price_versions.limits.story has no cell for the episode's {row.Tier} tier and {row.Language} language."));
        }

        var audit = ParseAudit(row.AuditReport);
        var content = ReadContent(row.Content);

        IReadOnlyList<string> warnings = content?.ContentWarnings ?? [];

        return new StoryGateReading(
            Version: new StoryVersionFacts(
                row.VersionId,
                row.WordCount,
                [.. row.Keywords],
                [.. warnings],
                audit.KeywordViolations,
                KeywordLimit(row.Tier),
                row.FactSheetId,
                approvedFactSheetId),
            Audit: audit.Facts,
            SourceSimilarity: row.SimilarityScore,
            RealWorldClaims: audit.RealWorldClaims,
            Series: new SeriesGateContext(EffectiveTopic(row.Topic, row.FactSheetId)),
            Length: new LengthBand(
                target.MinTokens,
                target.MaxTokens,
                (decimal)StoryQualityThresholds.Defaults.LengthTolerance),
            Moderation: audit.Moderation,
            Content: content,
            StoryText: content is null ? null : SimilarityText.From(content),
            ProfileRef: ReadGateThresholdsRef(row.AutopilotSettings),
            PriceVersion: active.Value.Version.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Assembles the Domain input from a reading plus the values only a port can answer: the past-episode score
    /// (check 10) and the two 02.8.2-T2 checks — the content gate's verdict (check 7) and the real-world detector's
    /// signal (check 8).
    /// </summary>
    /// <param name="reading">The stored facts, from <see cref="ReadAsync"/>.</param>
    /// <param name="pastEpisodeSimilarity">The score against the series' own history, or null when none was scorable.</param>
    /// <param name="moderation">
    /// The check-7 verdict over the current content, or null when the gate could not run at all — a null fails
    /// check 7 closed.
    /// </param>
    /// <param name="realWorld">
    /// The check-8 signal over the current content and the stored claims, or null when either could not be read —
    /// a null fails check 8 closed.
    /// </param>
    /// <param name="attempt">The story-gate attempt of the run, 1-based (D17).</param>
    /// <returns>The input <see cref="StoryGateRules.Evaluate"/> reads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    public static StoryGateInput Compose(
        StoryGateReading reading,
        decimal? pastEpisodeSimilarity,
        ModerationGateOutcome? moderation,
        RealWorldSignal? realWorld,
        int attempt)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return new StoryGateInput(
            reading.Version,
            reading.Audit,
            new SimilaritySignals(reading.SourceSimilarity, pastEpisodeSimilarity, PastEpisodeCorpusMax),
            moderation,
            realWorld,
            reading.Series,
            reading.Length,
            attempt);
    }

    /// <summary>
    /// The topic the story gate evaluates a version as: <c>true_crime</c> for a true-crime series <em>or</em> for a
    /// version written from a fact sheet (SEC-1, [DECISION], <c>StoryApprovalGate</c> 02.3.3-T1).
    /// </summary>
    /// <remarks>
    /// The pinned-data rule exists so patching a series' topic from <c>true_crime</c> to <c>fiction</c> cannot walk a
    /// real-events story past the checks that guard it: checks 14 and 15 and the hold are decided from
    /// <see cref="SeriesGateContext.Topic"/>, so a fact-sheet-backed version under a re-patched series is evaluated —
    /// and held — as true crime, exactly as <c>ApproveStoryCommandHandler</c>'s guard treats it. The evaluator uses
    /// the same answer for its approved-fact-sheet lookup, so check 14 compares two ids rather than a null.
    /// </remarks>
    /// <param name="topic">The series' stored topic.</param>
    /// <param name="factSheetId">The version's fact sheet id, or null when it was written without one.</param>
    /// <returns><see cref="Topic.TrueCrime"/>, or <paramref name="topic"/> when neither side is true crime.</returns>
    public static Topic EffectiveTopic(Topic topic, Guid? factSheetId) =>
        topic == Topic.TrueCrime || factSheetId is not null ? Topic.TrueCrime : topic;

    /// <summary>
    /// The check-12 cap <c>ceil(D × K)</c> (P5/F09): the tier's headline length in minutes (README P6: short ≤ 1,
    /// standard ≤ 8, long ≤ 15) times K = 6 images per minute.
    /// </summary>
    /// <remarks>
    /// <see cref="StoryQualityChecker"/> (Q-KW-02) and <c>RenderImagePlanner</c> compute the same cap from the
    /// same mapping, but each keeps it in a private helper, and the plan fixes K = 6
    /// (<see cref="StoryQualityThresholds.Defaults"/> is the Domain's own copy of it). The mapping is therefore
    /// restated here; a Domain <c>LengthTier</c> helper would let all three share it.
    /// </remarks>
    /// <param name="tier">The episode's length tier.</param>
    /// <returns>The keyword total the version may carry.</returns>
    public static int KeywordLimit(LengthTier tier)
    {
        var minutes = tier switch
        {
            LengthTier.Short => 1,
            LengthTier.Standard => 8,
            LengthTier.Long => 15,
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown length tier."),
        };

        return (int)Math.Ceiling(minutes * (double)StoryQualityThresholds.Defaults.KeywordsPerMinute);
    }

    // Null on an unreadable document, which is the state the caller treats as "no content": the similarity and
    // moderation checks that read the story text fail closed, and the gate moderates nothing.
    private static StoryContentV1? ReadContent(string content) =>
        StoryContentJson.TryReadStored(content, out var parsed) ? parsed : null;

    // The whole audit.v1 read: the three blocks the story gate consumes, each failing closed on its own.
    private static AuditReading ParseAudit(string? auditReportJson)
    {
        var root = ParseDocument(auditReportJson);
        if (root is null)
        {
            // A missing, truncated or foreign document: every check that reads the audit fails closed, and the
            // real-world claims, the issues and the keyword violations are unreadable rather than clean.
            return new AuditReading(
                new AuditFacts(
                    ModelReviewPresent: false,
                    ModelReviewStale: true,
                    Scores: null,
                    Issues: null,
                    ModerationFlagged: null,
                    RevisionApplied: null),
                StoredModerationVerdict.None,
                RealWorldClaims: null,
                KeywordViolations: null);
        }

        var review = root[ModelReviewMember] as JsonObject;
        var revision = root[RevisionMember] as JsonObject;
        var applied = ReadFlag(revision?[AppliedMember]);

        // 02.6.2-T1 (D22), the same rule as StoryReadProjection.Review: with an applied revision the current
        // state is review #2. F14: an absent or half-written review_after is not review #2 — the text was
        // rewritten, so its scores and its issues are unknown (null) and checks 2–4 fail closed. The rule never
        // falls back to model_review, whose review belongs to the text the revision replaced.
        var reviewAfter = applied == true ? revision?[ReviewAfterMember] as JsonObject : null;
        var selected = applied == true && !IsCompleteReview(reviewAfter) ? null : reviewAfter ?? review;

        // F4: the `local` block is read whole. A block that is absent, is not an object or carries no `issues`
        // array is unreadable — both writers always store it (StoryGenerationPipeline, EditStoryCommandHandler)
        // and StoryReadProjection reads a missing block as unreadable — so the issues of check 4 and the keyword
        // violations of check 12 are unknown (null) and both checks fail closed.
        var local = ReadLocalIssues(root[LocalMember]);
        var issues = selected is null || local is null ? null : ReadIssues(local, selected[IssuesMember]);
        var moderation = ReadModeration(root[ModerationMember]);

        return new AuditReading(
            new AuditFacts(
                ModelReviewPresent: review is not null,
                // A missing flag is stale (the projection's rule), so an edited story fails check 1.
                ModelReviewStale: ReadFlag(review?[StaleMember]) is not false,
                Scores: selected is null ? null : ReadScores(selected[ScoresMember]),
                Issues: issues,
                // A block that is not a complete, current verdict — missing, malformed, stale or unavailable
                // (StoryDraftModeration.NeedsRecheck, D12) — is no evidence that the draft was clean: check 6 fails
                // closed. Check 7 no longer reads this block at all (02.8.2-T2 checks the current content itself).
                ModerationFlagged: moderation.Usable ? moderation.Flagged : null,
                RevisionApplied: revision is null ? false : applied),
            moderation,
            ReadClaims(review, reviewAfter),
            KeywordViolations: local is null ? null : CountKeywordViolations(local));
    }

    // Whether a revision's review #2 is a complete block, the selection rule of StoryReadProjection.Review: only a
    // review_after that carries both its scores and its issues is the current state (F14).
    private static bool IsCompleteReview(JsonObject? reviewAfter) =>
        reviewAfter is { } after && after[ScoresMember] is JsonObject && after[IssuesMember] is JsonArray;

    // The `issues` member of the local block, or null when the block or the member is absent or of another type:
    // an unreadable local block is unknown, never an empty list (F4).
    private static JsonArray? ReadLocalIssues(JsonNode? localBlock) =>
        localBlock is JsonObject block && block[IssuesMember] is JsonArray issues ? issues : null;

    // The one member check 6 reads. StoryDraftModeration owns the whole shape (flagged/categories/blocking/stale/
    // unavailable) and the "needs a fresh check" rule, so the gate and the approval re-check agree on what a usable
    // verdict is; check 7 no longer reuses this block at all (02.8.2-T2 checks the current content itself).
    private static StoredModerationVerdict ReadModeration(JsonNode? node)
    {
        if (StoryDraftModeration.NeedsRecheck(node) || node is not JsonObject moderation)
        {
            return StoredModerationVerdict.None;
        }

        return new StoredModerationVerdict(true, ReadFlag(moderation[FlaggedMember]) ?? false);
    }

    // Check 8 (02.8.2-T2, plan §3.1): the claims of the model review and of a complete revision.review_after,
    // unioned and reduced to their D22 addresses, because the revision rewrote the text the first review's claims
    // were about (an extra owner review is a cost, a missed claim is a hole). A review that does not carry the
    // member — or carries something that is not an array — makes the whole answer unknown: AuditReportBuilder
    // always writes it (F6), so an absent one is a foreign or truncated document, and check 8 fails closed. A
    // present but empty array is still the clean signal "the model claimed nothing". The claims' own wording is
    // never copied here (D19/D22).
    private static List<GateClaim>? ReadClaims(JsonObject? review, JsonObject? reviewAfter)
    {
        if (review is null)
        {
            return null;
        }

        var claims = new List<GateClaim>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!AppendClaims(review, claims, seen))
        {
            return null;
        }

        if (IsCompleteReview(reviewAfter) && !AppendClaims(reviewAfter!, claims, seen))
        {
            return null;
        }

        return claims;

        static bool AppendClaims(JsonObject block, List<GateClaim> claims, HashSet<string> seen)
        {
            if (block[RealWorldClaimsMember] is not JsonArray array)
            {
                return false;
            }

            foreach (var claim in array)
            {
                var segment = Cap(claim is JsonObject entry ? ReadText(entry[SegmentMember]) : null);

                // One entry per address: the same segment claimed by both reviews is one place to look at, and
                // the detector's own address lookup is case-insensitive. A claim with no readable address keeps
                // one entry each, because each of them asserted something.
                if (segment.Length == 0 || seen.Add(segment))
                {
                    claims.Add(new GateClaim(segment));
                }
            }

            return true;
        }
    }

    // local.issues ∪ the selected review's issues (plan §3.1 check 4). Both sides must be arrays: a selected
    // review without an `issues` array is as unreadable as an absent local block (AuditReportBuilder always writes
    // it), so the check fails closed rather than counting one side only. An entry whose severity or kind is
    // unreadable is carried as an empty string, which the Domain rule counts as blocking (F5), so a malformed
    // issue can never open the gate.
    private static List<GateIssueFacts>? ReadIssues(JsonArray local, JsonNode? reviewIssues)
    {
        if (reviewIssues is not JsonArray review)
        {
            return null;
        }

        var issues = new List<GateIssueFacts>(local.Count + review.Count);
        Append(local);
        Append(review);

        return issues;

        void Append(JsonArray array)
        {
            foreach (var entry in array)
            {
                var issue = entry as JsonObject;
                issues.Add(new GateIssueFacts(
                    ReadText(issue?[SeverityMember]) ?? string.Empty,
                    ReadText(issue?[KindMember]) ?? string.Empty));
            }
        }
    }

    // Check 12's violations. The stored audit is the only source: the two blocked-keyword rules of
    // StoryDraftValidator (D3, blocking) and StoryQualityChecker (Q-KW-03, advisory) both mean a keyword broke
    // KeywordRules, and the gate must not re-run the checker (StoryVersionFacts.KeywordViolationCount). The local
    // block is read, never empty-by-absence: an unreadable one leaves the count unknown (F4).
    private static int CountKeywordViolations(JsonArray localIssues)
    {
        var violations = 0;
        foreach (var entry in localIssues)
        {
            var rule = entry is JsonObject issue ? ReadText(issue[RuleMember]) : null;
            if (string.Equals(rule, StoryQualityChecker.KeywordInvalid, StringComparison.Ordinal)
                || string.Equals(rule, StoryDraftValidator.KeywordInvalid, StringComparison.Ordinal))
            {
                violations++;
            }
        }

        return violations;
    }

    // The D16 scores of one review. All six required dimensions must be whole numbers inside 1..5 and
    // factual_care must be a number or absent, exactly as ReviewResultValidator accepted them; anything else is
    // null, so checks 2 and 3 fail with field_absent rather than comparing against a partly invented scorecard.
    private static ReviewScores? ReadScores(JsonNode? node)
    {
        if (node is not JsonObject scores
            || !ReadScore(scores, "hook", out var hook)
            || !ReadScore(scores, "pacing", out var pacing)
            || !ReadScore(scores, "atmosphere", out var atmosphere)
            || !ReadScore(scores, "natural_language", out var naturalLanguage)
            || !ReadScore(scores, "originality", out var originality)
            || !ReadOptionalScore(scores, "factual_care", out var factualCare)
            || !ReadScore(scores, "length_fit", out var lengthFit))
        {
            return null;
        }

        return new ReviewScores(hook, pacing, atmosphere, naturalLanguage, originality, factualCare, lengthFit);
    }

    private static bool ReadScore(JsonObject scores, string name, out int value)
    {
        value = 0;
        return scores[name] is JsonValue node
            && node.TryGetValue<int>(out value)
            && value is >= ReviewScores.Min and <= ReviewScores.Max;
    }

    private static bool ReadOptionalScore(JsonObject scores, string name, out int? value)
    {
        value = null;
        if (scores[name] is null)
        {
            return true;
        }

        if (!ReadScore(scores, name, out var measured))
        {
            return false;
        }

        value = measured;
        return true;
    }

    /// <summary>
    /// The <c>gate_thresholds_ref</c> of a series' <c>autopilot_settings</c> document (plan §3.1, §3.5): the profile
    /// name the thresholds resolve through.
    /// </summary>
    /// <remarks>
    /// The one implementation of this read (F11): <see cref="VideoGateInputFactory"/> calls
    /// <see cref="ReadGateThresholdsRef(string, SettingsFault)"/> so the two factories cannot drift. The read is
    /// defensive: 02.8.3-T1 owns the document and has no typed member for it, so an absent, unreadable or foreign
    /// document — or a key that is absent or not a non-blank string — is <see langword="null"/>.
    /// </remarks>
    /// <param name="autopilotSettings">The stored <c>series.autopilot_settings</c> text, or null when there is none.</param>
    /// <returns>
    /// The profile name, or <see langword="null"/>. The caller resolves null to the <c>default</c> profile and never
    /// disables the gate (plan §10 decision 6).
    /// </returns>
    public static string? ReadGateThresholdsRef(string? autopilotSettings) =>
        ReadGateThresholdsRef(autopilotSettings, out _);

    /// <summary>
    /// The same read, reporting why an unreadable document could not be read so a caller that logs can say it
    /// (F11); <see cref="VideoGateInputFactory.ReadGateThresholdsRef"/> is the only such caller.
    /// </summary>
    /// <param name="autopilotSettings">The stored <c>series.autopilot_settings</c> text, or null when there is none.</param>
    /// <param name="fault">
    /// <see cref="SettingsFault.None"/> when the document was read or was simply absent, otherwise the reason it
    /// could not be read.
    /// </param>
    /// <returns>The profile name, or <see langword="null"/>.</returns>
    internal static string? ReadGateThresholdsRef(string? autopilotSettings, out SettingsFault fault)
    {
        fault = SettingsFault.None;
        if (string.IsNullOrWhiteSpace(autopilotSettings))
        {
            return null;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(autopilotSettings);
        }
        catch (JsonException)
        {
            fault = SettingsFault.Unreadable;

            return null;
        }

        if (parsed is not JsonObject settings)
        {
            fault = SettingsFault.NotAnObject;

            return null;
        }

        var name = ReadText(settings[GateThresholdsRefMember]);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static JsonObject? ParseDocument(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A JSON boolean member, or null when it is absent or of another type. A string "true" is not a true (the
    // writer stores booleans), so a coerced value can never open a check.
    private static bool? ReadFlag(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static string? ReadText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Cap(string? value)
    {
        var clean = TextSafety.Clean(value);
        return clean.Length <= MaxSegmentChars ? clean : clean[..MaxSegmentChars];
    }

    /// <summary>The stored facts of one <c>story_versions</c> row and the series it belongs to.</summary>
    /// <param name="VersionId">The row's id.</param>
    /// <param name="WordCount">The D4 token count of the stored body (check 11).</param>
    /// <param name="Keywords">The stored keywords; only their count and their violations are read (check 12).</param>
    /// <param name="Content">The stored <c>content</c> document (check 13, the similarity projection, the fallback moderation).</param>
    /// <param name="AuditReport">The stored <c>audit_report</c>, or null when the row has none (checks 1–6, 8).</param>
    /// <param name="SimilarityScore"><c>story_versions.similarity_score</c> (check 9), or null when it was never computed.</param>
    /// <param name="FactSheetId">The sheet the version was written from (check 14), or null.</param>
    /// <param name="Topic">The series topic: <c>true_crime</c> adds checks 14 and 15 and holds the story.</param>
    /// <param name="Language">The series language: the D4 band is per language (check 11).</param>
    /// <param name="Tier">The episode's length tier: the band cell and the keyword cap.</param>
    /// <param name="AutopilotSettings">The series' <c>autopilot_settings</c>, the source of the profile reference.</param>
    /// <param name="CreatedAt">
    /// When the version was written; the instant the D4 band is resolved at, so the gate reads the band the writer
    /// was given (02 README D14: a re-price is a new version row).
    /// </param>
    public sealed record StoryGateRow(
        Guid VersionId,
        int WordCount,
        IReadOnlyList<string> Keywords,
        string Content,
        string? AuditReport,
        decimal? SimilarityScore,
        Guid? FactSheetId,
        Topic Topic,
        Language Language,
        LengthTier Tier,
        string? AutopilotSettings,
        DateTimeOffset CreatedAt);

    /// <summary>What the factory read from one row: everything except the past-episode score and the two 02.8.2-T2 checks.</summary>
    /// <param name="Version">The version facts (checks 11–14).</param>
    /// <param name="Audit">The audit facts, already selected per 02.6.2-T1 (checks 1–6).</param>
    /// <param name="SourceSimilarity">The stored source similarity (check 9).</param>
    /// <param name="RealWorldClaims">
    /// The review model's claims reduced to their D22 addresses, unioned across <c>model_review</c> and a complete
    /// <c>revision.review_after</c>, or <see langword="null"/> when the stored audit report did not let them be read
    /// (check 8 fails closed).
    /// </param>
    /// <param name="Series">The series facts (the <c>true_crime</c> hold).</param>
    /// <param name="Length">The D4 band of the version's tier × language cell (check 11).</param>
    /// <param name="Moderation">The stored draft-time moderation verdict (check 6), usable or not.</param>
    /// <param name="Content">
    /// The parsed <c>story.v1</c> document, or <see langword="null"/> when it could not be read: the content gate
    /// (check 7) and the real-world detector (check 8) both read this one parse, and an unreadable document leaves
    /// both with nothing to check — which they fail closed on.
    /// </param>
    /// <param name="StoryText">The version's own segments, for the past-episode score, or null when the content is unreadable.</param>
    /// <param name="ProfileRef">The series' gate-thresholds profile reference, or null for the <c>default</c> profile.</param>
    /// <param name="PriceVersion">
    /// The active price version at the version's creation instant, as text: the D4 band was resolved with it, and the
    /// gate's free moderation call is metered against it (02 README D14).
    /// </param>
    public sealed record StoryGateReading(
        StoryVersionFacts Version,
        AuditFacts Audit,
        decimal? SourceSimilarity,
        IReadOnlyList<GateClaim>? RealWorldClaims,
        SeriesGateContext Series,
        LengthBand Length,
        StoredModerationVerdict Moderation,
        StoryContentV1? Content,
        SimilarityText? StoryText,
        string? ProfileRef,
        string PriceVersion);

    /// <summary>
    /// The stored draft-time moderation verdict as check 6 reads it (plan §3.1 check 6).
    /// </summary>
    /// <remarks>
    /// Only the flag is kept: check 6 asks whether the draft-time check passed, and check 7 reads the current
    /// content through the content gate instead of reusing this block (02.8.2-T2). An unusable block is not a
    /// clean one — <see cref="AuditFacts.ModerationFlagged"/> stays <see langword="null"/> and check 6 fails closed.
    /// </remarks>
    /// <param name="Usable">
    /// Whether the block is a complete, current verdict (<see cref="StoryDraftModeration.NeedsRecheck"/> says no).
    /// </param>
    /// <param name="Flagged">The verdict's flag; false when there is no usable verdict.</param>
    public sealed record StoredModerationVerdict(bool Usable, bool Flagged)
    {
        /// <summary>No usable stored verdict.</summary>
        public static StoredModerationVerdict None { get; } = new(false, false);
    }

    /// <summary>The three blocks one <c>audit.v1</c> read produces.</summary>
    /// <param name="Facts">The facts checks 1–6 read.</param>
    /// <param name="Moderation">The stored moderation verdict (check 6).</param>
    /// <param name="RealWorldClaims">The review model's claims by D22 address (check 8), or null when they could not be read.</param>
    /// <param name="KeywordViolations">
    /// The stored blocked-keyword issues (check 12), or <see langword="null"/> when the local block could not be
    /// read, which fails the check closed.
    /// </param>
    private sealed record AuditReading(
        AuditFacts Facts,
        StoredModerationVerdict Moderation,
        IReadOnlyList<GateClaim>? RealWorldClaims,
        int? KeywordViolations);

    /// <summary>
    /// Why a stored <c>autopilot_settings</c> document could not be read, so the one caller that logs
    /// (<see cref="VideoGateInputFactory.ReadGateThresholdsRef"/>) can say which case it was (F11).
    /// </summary>
    internal enum SettingsFault
    {
        /// <summary>The document was read, or there was none to read.</summary>
        None,

        /// <summary>The stored text is not JSON.</summary>
        Unreadable,

        /// <summary>The stored text parses but is not a JSON object.</summary>
        NotAnObject,
    }
}
