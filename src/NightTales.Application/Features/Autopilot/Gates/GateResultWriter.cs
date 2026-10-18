using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The read-modify-write of <c>autopilot_runs.gate_results</c> (02.8.2 plan §3.4, task step 4): it merges one
/// half of the <c>gates.v1</c> document into the stored one and hands the whole document back to the aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two writers, one document.</b> The story verdict and the video verdict of a run arrive hours apart, so
/// neither may replace the other: a write loads the stored document, replaces only the block it owns (plus the
/// three context lines) and keeps everything else verbatim
/// (<c>GateResultWriter_MergesStoryThenVideo_PreservesStoryChecks</c>).
/// </para>
/// <para>
/// <b>Never a wrong pass.</b> A stored document that is missing, empty, not an object or not JSON at all is
/// treated as absent and a fresh <c>gates.v1</c> document is started: a half-written row degrades to "re-evaluate",
/// never to an unreadable verdict the run log has to guess at. The one thing that is never overwritten is a
/// higher story <c>attempt</c>: the gate evaluates at most two generations per run (D17), so a late write of the
/// first attempt must not erase the second one's verdict — it is ignored and the call still succeeds, because the
/// caller's verdict was already superseded (<c>GateResultWriter_SecondStoryAttempt_KeepsHigherAttempt</c>).
/// </para>
/// <para>
/// <b>Tracked, never committed.</b> The aggregate is loaded through the write model, so the change is visible to
/// the caller's unit of work and commits with it — the writer itself never calls <c>SaveChangesAsync</c>
/// (09 README D5). <see cref="GateOutcomeRecorder"/> is the transaction that pairs it with the notice.
/// </para>
/// <para>
/// <b>Ids, codes and numbers only.</b> Every value written here comes from the Domain verdict or from
/// <see cref="GateStoryEvidence"/>: never story text, a brief, a fact, an evidence excerpt or a source URL
/// (D19/D22).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model the run is loaded from.</param>
/// <param name="clock">The clock that stamps <c>evaluated_at</c>.</param>
public sealed class GateResultWriter(IAppDbContext db, IClock clock)
{
    /// <summary>The document's own version key, written by every merge (02.8.2 plan §3.4).</summary>
    public const string SchemaVersion = "gates.v1";

    /// <summary>The <c>story</c> block and the gate name <see cref="AppendFailureAsync"/> takes for it.</summary>
    public const string StoryGate = "story";

    /// <summary>The <c>video</c> block and the gate name <see cref="AppendFailureAsync"/> takes for it.</summary>
    public const string VideoGate = "video";

    /// <summary>The key of the story block.</summary>
    public const string StoryKeyName = "story";

    /// <summary>The key of the video block.</summary>
    public const string VideoKeyName = "video";

    /// <summary>The key of the similarity context block.</summary>
    public const string SimilarityKeyName = "similarity";

    /// <summary>The key of the real-world context block.</summary>
    public const string RealWorldKeyName = "real_world";

    /// <summary>The key of the moderation context block.</summary>
    public const string ModerationKeyName = "moderation";

    /// <summary>The key the document version is written under.</summary>
    public const string SchemaVersionKeyName = "schema_version";

    /// <summary>The key the gate mode is written under.</summary>
    public const string ModeKeyName = "mode";

    /// <summary>The key the effective profile name is written under.</summary>
    public const string ProfileKeyName = "profile";

    /// <summary>The key the evaluation instant is written under.</summary>
    public const string EvaluatedAtKeyName = "evaluated_at";

    /// <summary>
    /// The document's keys in the order plan §3.4 writes them. A merge emits exactly this order and keeps a key
    /// this version does not know of after them, so a later feature's block survives an older writer.
    /// </summary>
    private static readonly string[] DocumentKeys =
    [
        SchemaVersionKeyName,
        ModeKeyName,
        ProfileKeyName,
        EvaluatedAtKeyName,
        StoryKeyName,
        VideoKeyName,
        SimilarityKeyName,
        RealWorldKeyName,
        ModerationKeyName,
    ];

    /// <summary>
    /// Merges the story verdict of one attempt into the run's <c>gate_results</c> (plan §3.4, task step 4):
    /// the <c>story</c> block, the <c>similarity</c>/<c>real_world</c>/<c>moderation</c> context of that
    /// evaluation and the document's mode, profile and timestamp.
    /// </summary>
    /// <param name="runId">The run whose verdict is stored.</param>
    /// <param name="result">The Domain verdict; it carries the attempt that guards the merge.</param>
    /// <param name="evidence">The context lines stored beside the verdict.</param>
    /// <param name="mode">The mode of this evaluation: <c>live</c> or <c>dry_run</c>.</param>
    /// <param name="profile">The effective profile name this evaluation used.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>Success, or the error that stopped the write.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="evidence"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is blank.</exception>
    public async ValueTask<ErrorOr<Success>> WriteStoryAsync(
        Guid runId,
        StoryGateResult result,
        GateStoryEvidence evidence,
        GateMode mode,
        string profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        var run = await FindAsync(runId, ct).ConfigureAwait(false);
        if (run.IsError)
        {
            return run.Errors;
        }

        var stored = Load(run.Value.GateResults);

        if (StoredAttempt(stored) is { } storedAttempt && storedAttempt > result.Attempt)
        {
            return Result.Success;
        }

        var document = Rebuild(stored, new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [SchemaVersionKeyName] = JsonValue.Create(SchemaVersion),
            [ModeKeyName] = JsonValue.Create(mode.ToWire()),
            [ProfileKeyName] = JsonValue.Create(profile),
            [EvaluatedAtKeyName] = JsonValue.Create(Timestamp()),
            [StoryKeyName] = StoryBlock(result, evidence),
            [SimilarityKeyName] = SimilarityBlock(evidence),
            [RealWorldKeyName] = RealWorldBlock(evidence),
            [ModerationKeyName] = ModerationBlock(evidence),
        });

        return Persist(run.Value, document);
    }

    /// <summary>
    /// Merges the video verdict of one run's finished render into its <c>gate_results</c> (plan §3.4, task
    /// step 4). The story block and the context lines of the story evaluation are kept verbatim: this write owns
    /// the <c>video</c> block, the mode, the profile and the timestamp and nothing else.
    /// </summary>
    /// <param name="runId">The run whose render was evaluated.</param>
    /// <param name="result">The Domain verdict of the 10 video checks.</param>
    /// <param name="mode">The mode of this evaluation: <c>live</c> or <c>dry_run</c>.</param>
    /// <param name="profile">The effective profile name this evaluation used.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>Success, or the error that stopped the write.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is blank.</exception>
    public async ValueTask<ErrorOr<Success>> WriteVideoAsync(
        Guid runId,
        VideoGateResult result,
        GateMode mode,
        string profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        var run = await FindAsync(runId, ct).ConfigureAwait(false);
        if (run.IsError)
        {
            return run.Errors;
        }

        var document = Rebuild(Load(run.Value.GateResults), new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [SchemaVersionKeyName] = JsonValue.Create(SchemaVersion),
            [ModeKeyName] = JsonValue.Create(mode.ToWire()),
            [ProfileKeyName] = JsonValue.Create(profile),
            [EvaluatedAtKeyName] = JsonValue.Create(Timestamp()),
            [VideoKeyName] = VideoBlock(result),
        });

        return Persist(run.Value, document);
    }

    /// <summary>
    /// Appends one failure code to a stored verdict's <c>failures[]</c> (plan §3.4, "Failure handling": a gate
    /// that passed its checks and was then refused by the approve command records the refusal on the run).
    /// </summary>
    /// <remarks>
    /// The document's mode, profile and <c>evaluated_at</c> are left alone: they belong to the evaluation this
    /// code is appended to, and <see cref="GateOutcomeRecorder"/> appends inside the same unit of work that wrote
    /// it. A code already in the list is not added twice. When the named gate has no block at all — the defensive
    /// case of an approval recorded before any verdict, which the recorder's own flow does not produce — a
    /// minimal failing block is written so the refusal is visible in the run log instead of being dropped.
    /// </remarks>
    /// <param name="runId">The run whose verdict is amended.</param>
    /// <param name="gate">The gate whose block is amended: <see cref="StoryGate"/> or <see cref="VideoGate"/>.</param>
    /// <param name="failureCode">The code to append, for example <c>etag_conflict</c>.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>Success, or the error that stopped the write.</returns>
    /// <exception cref="ArgumentException"><paramref name="gate"/> or <paramref name="failureCode"/> is blank.</exception>
    public async ValueTask<ErrorOr<Success>> AppendFailureAsync(
        Guid runId,
        string gate,
        string failureCode,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);

        var run = await FindAsync(runId, ct).ConfigureAwait(false);
        if (run.IsError)
        {
            return run.Errors;
        }

        var stored = Load(run.Value.GateResults);
        var document = Rebuild(stored, new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [gate] = BlockWithFailure(stored[gate] as JsonObject, failureCode),
        });

        return Persist(run.Value, document);
    }

    private static JsonObject StoryBlock(StoryGateResult result, GateStoryEvidence evidence) => new()
    {
        ["pass"] = JsonValue.Create(result.Pass),
        ["hold"] = JsonValue.Create(result.Hold),
        ["attempt"] = JsonValue.Create(result.Attempt),
        ["story_version_id"] = JsonValue.Create(result.StoryVersionId.ToString()),
        ["episode_status_at_evaluation"] = JsonValue.Create(evidence.EpisodeStatusAtEvaluation),
        ["checks"] = Checks(result.Checks),
        ["failures"] = Strings(result.Failures),
    };

    private static JsonObject VideoBlock(VideoGateResult result) => new()
    {
        ["pass"] = JsonValue.Create(result.Pass),
        ["render_job_id"] = JsonValue.Create(result.RenderJobId.ToString()),
        ["asset_id"] = JsonValue.Create(result.AssetId.ToString()),
        ["checks"] = Checks(result.Checks),
        ["failures"] = Strings(result.Failures),
    };

    private static JsonObject SimilarityBlock(GateStoryEvidence evidence) => new()
    {
        ["sources"] = JsonValue.Create(evidence.SourceSimilarity),
        ["past_episodes"] = JsonValue.Create(evidence.PastEpisodeSimilarity),
        ["corpus_max"] = JsonValue.Create(evidence.CorpusMax),
    };

    private static JsonObject RealWorldBlock(GateStoryEvidence evidence)
    {
        var signals = new JsonArray();
        foreach (var kind in evidence.RealWorldKinds)
        {
            signals.Add(JsonValue.Create(kind));
        }

        return new JsonObject
        {
            ["hit"] = JsonValue.Create(evidence.RealWorldHit),
            ["signals"] = signals,
        };
    }

    private static JsonObject ModerationBlock(GateStoryEvidence evidence)
    {
        var block = new JsonObject
        {
            ["flagged"] = JsonValue.Create(evidence.ModerationFlagged),
            ["categories"] = Strings(evidence.ModerationCategories),
        };

        if (evidence.ModerationUnavailableDetail is { } detail)
        {
            block["unavailable_detail"] = JsonValue.Create(detail);
        }

        if (evidence.ModerationCheckedAt is { } checkedAt)
        {
            block["checked_at"] = JsonValue.Create(Format(checkedAt));
        }

        return block;
    }

    private static JsonArray Checks(IReadOnlyList<GateCheck> checks)
    {
        var array = new JsonArray();

        foreach (var check in checks)
        {
            array.Add(new JsonObject
            {
                ["id"] = JsonValue.Create(check.Id),
                ["value"] = JsonValue.Create(check.Value),
                ["threshold"] = JsonValue.Create(check.Threshold),
                ["pass"] = JsonValue.Create(check.Pass),
                ["detail"] = JsonValue.Create(check.Detail),
            });
        }

        return array;
    }

    private static JsonArray Strings(IReadOnlyList<string> values)
    {
        var array = new JsonArray();

        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    /// <summary>The named block with its <c>failures[]</c> extended, or a minimal failing block when there is none.</summary>
    /// <param name="block">The stored block, or <see langword="null"/> when the gate never wrote one.</param>
    /// <param name="failureCode">The code to append.</param>
    /// <returns>A detached block, ready to be written into a rebuilt document.</returns>
    private static JsonObject BlockWithFailure(JsonObject? block, string failureCode)
    {
        var failures = new JsonArray();

        if (block is null)
        {
            failures.Add(JsonValue.Create(failureCode));

            return new JsonObject
            {
                ["pass"] = JsonValue.Create(false),
                ["failures"] = failures,
            };
        }

        var updated = block.DeepClone().AsObject();

        if (updated["failures"] is JsonArray stored)
        {
            foreach (var existing in stored)
            {
                if (string.Equals(existing?.GetValue<string>(), failureCode, StringComparison.Ordinal))
                {
                    return updated;
                }

                failures.Add(JsonValue.Create(existing?.GetValue<string>()));
            }
        }

        failures.Add(JsonValue.Create(failureCode));
        updated["failures"] = failures;

        return updated;
    }

    /// <summary>
    /// Rebuilds the document in plan §3.4 key order: the canonical keys with the caller's overrides in their
    /// canonical position, the stored blocks this write does not own carried over verbatim, and any key this
    /// version does not know of kept after them, so a future block is never dropped by an older writer.
    /// </summary>
    /// <param name="stored">The document as it was stored; an unusable one is treated as empty by the caller.</param>
    /// <param name="overrides">
    /// The blocks and context lines this write owns. Every key must be one of <see cref="DocumentKeys"/>; a key
    /// outside the canonical set would be silently dropped, so callers pass these as literals.
    /// </param>
    /// <returns>A detached document, ready to be stored.</returns>
    private static JsonObject Rebuild(JsonObject stored, Dictionary<string, JsonNode?> overrides)
    {
        var document = new JsonObject();

        foreach (var key in DocumentKeys)
        {
            if (overrides.TryGetValue(key, out var value))
            {
                if (value is not null)
                {
                    document[key] = value;
                }

                continue;
            }

            if (stored[key] is { } carried)
            {
                document[key] = carried.DeepClone();
            }
        }

        foreach (var (key, value) in stored)
        {
            if (!DocumentKeys.Contains(key, StringComparer.Ordinal) && value is { } unknown)
            {
                document[key] = unknown.DeepClone();
            }
        }

        return document;
    }

    /// <summary>The <c>attempt</c> of the stored story block, or <see langword="null"/> when there is none.</summary>
    /// <param name="stored">The stored document.</param>
    /// <returns>The stored attempt.</returns>
    private static int? StoredAttempt(JsonObject stored) =>
        stored[StoryKeyName] is JsonObject story
        && story["attempt"] is JsonValue attempt
        && attempt.TryGetValue<int>(out var value)
            ? value
            : null;

    /// <summary>
    /// Parses the stored column defensively: a missing, blank, non-object or malformed document becomes a fresh
    /// empty one, so the merge starts a <c>gates.v1</c> document instead of failing the gate.
    /// </summary>
    /// <param name="stored">The column as it was read.</param>
    /// <returns>A document to merge into; never <see langword="null"/>.</returns>
    private static JsonObject Load(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(stored) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private async ValueTask<ErrorOr<AutopilotRun>> FindAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.AutopilotRuns
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, ct)
            .ConfigureAwait(false);

        return run is null ? GateErrors.RunNotFound : run;
    }

    private static ErrorOr<Success> Persist(AutopilotRun run, JsonObject document)
    {
        var recorded = run.RecordGateResults(document.ToJsonString());

        return recorded.IsError ? recorded.Errors : Result.Success;
    }

    private string Timestamp() => Format(clock.UtcNow);

    private static string Format(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
