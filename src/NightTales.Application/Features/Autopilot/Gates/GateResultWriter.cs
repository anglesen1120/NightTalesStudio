using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// What one merge did with the verdict it was handed (02.8.2-T1 F8): whether the stored document changed, already
/// held exactly this verdict, or was owned by a later attempt.
/// </summary>
/// <remarks>
/// The caller's next step depends on it. <see cref="Written"/> means the stored verdict is new and the owner's
/// notice for it has not been queued yet; <see cref="Unchanged"/> and <see cref="Superseded"/> mean a second
/// <c>autopilot.owner_action_required</c> row for the same stop would be a duplicate, so
/// <see cref="GateOutcomeRecorder"/> records the verdict and notifies nobody.
/// </remarks>
public enum GateWriteResult
{
    /// <summary>The stored <c>gates.v1</c> document now carries this verdict.</summary>
    Written,

    /// <summary>The document already holds exactly this verdict for this gate: nothing was written.</summary>
    Unchanged,

    /// <summary>A later story attempt owns the stored verdict: this older attempt's write was ignored.</summary>
    Superseded,
}

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
/// first attempt must not erase the second one's verdict — it is ignored and answered with
/// <see cref="GateWriteResult.Superseded"/> (<c>GateResultWriter_SecondStoryAttempt_KeepsHigherAttempt</c>).
/// </para>
/// <para>
/// <b>It reports what the merge did, and refuses what PostgreSQL would refuse.</b> Every write answers whether the
/// stored document changed (<see cref="GateWriteResult"/>), so the caller can tell a first verdict from the
/// re-evaluation of one it already holds and does not queue the owner's notice twice (02.8.2-T1 F8). A merged
/// document that carries a value <c>jsonb</c> refuses — a <c>NUL</c>, an unpaired surrogate — is answered with
/// <see cref="NotPersistableErrorCode"/> instead of an exception at commit, so one unreadable vendor string cannot
/// turn the run into a poison pill (02.8.2-T1 SEC-8).
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

    /// <summary>
    /// The fixed code a merge answers when the document it would store carries a value PostgreSQL's <c>jsonb</c>
    /// refuses (a <c>NUL</c> or an unpaired surrogate, 02.8.2-T1 SEC-8). Nothing is written in that case: the
    /// alternative is an exception at commit, which would roll back the verdict's whole transaction on every tick.
    /// </summary>
    public const string NotPersistableErrorCode = "autopilot_gate_document_not_persistable";

    /// <summary>The most characters of a stored value a gate log line may carry (02.8.2-T1 SEC-6).</summary>
    public const int LogSafeMaxLength = 40;

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
    /// <returns>What the merge did, or the error that stopped the write.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="evidence"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is blank.</exception>
    public async ValueTask<ErrorOr<GateWriteResult>> WriteStoryAsync(
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
            return GateWriteResult.Superseded;
        }

        var block = StoryBlock(result, evidence);
        if (IsSameStoredVerdict(stored, StoryKeyName, block, mode))
        {
            return GateWriteResult.Unchanged;
        }

        var document = Rebuild(stored, new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [SchemaVersionKeyName] = JsonValue.Create(SchemaVersion),
            [ModeKeyName] = JsonValue.Create(mode.ToWire()),
            [ProfileKeyName] = JsonValue.Create(profile),
            [EvaluatedAtKeyName] = JsonValue.Create(Timestamp()),
            [StoryKeyName] = block,
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
    /// <returns>What the merge did, or the error that stopped the write.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is blank.</exception>
    public async ValueTask<ErrorOr<GateWriteResult>> WriteVideoAsync(
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

        var stored = Load(run.Value.GateResults);
        var block = VideoBlock(result);
        if (IsSameStoredVerdict(stored, VideoKeyName, block, mode))
        {
            return GateWriteResult.Unchanged;
        }

        var document = Rebuild(stored, new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [SchemaVersionKeyName] = JsonValue.Create(SchemaVersion),
            [ModeKeyName] = JsonValue.Create(mode.ToWire()),
            [ProfileKeyName] = JsonValue.Create(profile),
            [EvaluatedAtKeyName] = JsonValue.Create(Timestamp()),
            [VideoKeyName] = block,
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
    /// <returns>What the merge did — <see cref="GateWriteResult.Unchanged"/> when the code is already there — or the error that stopped the write.</returns>
    /// <exception cref="ArgumentException"><paramref name="gate"/> or <paramref name="failureCode"/> is blank.</exception>
    public async ValueTask<ErrorOr<GateWriteResult>> AppendFailureAsync(
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
        var storedBlock = stored[gate] as JsonObject;
        var updated = BlockWithFailure(storedBlock, failureCode);

        // The same code on the same block is the same refusal recorded twice: the caller's notice for it is already
        // queued, and a second stop with the same reason must not mail the owner again (02.8.2-T1 F8).
        if (storedBlock is not null && JsonNode.DeepEquals(storedBlock, updated))
        {
            return GateWriteResult.Unchanged;
        }

        var document = Rebuild(stored, new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [gate] = updated,
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

        var block = new JsonObject
        {
            ["hit"] = JsonValue.Create(evidence.RealWorldHit),
            ["signals"] = signals,
        };

        // Additive (02.8.2-T2): where the signals fired, as the D22 ids the editor jumps to. Omitted when empty, so
        // a clean story keeps exactly the plan's `{"hit": false, "signals": []}` shape.
        if (evidence.RealWorldSegments.Count > 0)
        {
            block["segments"] = Strings(Storable(evidence.RealWorldSegments));
        }

        return block;
    }

    private static JsonObject ModerationBlock(GateStoryEvidence evidence)
    {
        var block = new JsonObject
        {
            ["flagged"] = JsonValue.Create(evidence.ModerationFlagged),
            ["categories"] = Strings(Storable(evidence.ModerationCategories)),
        };

        // Additive (02.8.2-T2): the ids the flag was attributed to, `scene:<i>` included. Ids only, and omitted on a
        // pass, so the plan's `{"flagged": false, "categories": []}` shape survives.
        if (evidence.ModerationSegments.Count > 0)
        {
            block["segments"] = Strings(Storable(evidence.ModerationSegments));
        }

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

    /// <summary>
    /// Whether the stored document already holds exactly the verdict this write carries for one gate (02.8.2-T1
    /// F8): the same block — checks, values, thresholds, details and failures included — under the same mode. The
    /// comparison is by value, so a document that PostgreSQL re-ordered and re-normalised on its way through
    /// <c>jsonb</c> still compares equal to the block just built.
    /// </summary>
    /// <remarks>
    /// The caller needs this to tell "a verdict the run already holds" from "a new one": a re-evaluation of a stop
    /// the owner was already told about must not queue a second notice. The mode is part of the comparison because
    /// it is what turns a passing verdict into a dry-run stop, and the block is what turns it into a failing one.
    /// </remarks>
    /// <param name="stored">The stored document; an unusable one is empty and never matches.</param>
    /// <param name="gateKey">The gate's block key: <see cref="StoryKeyName"/> or <see cref="VideoKeyName"/>.</param>
    /// <param name="block">The block this write would store.</param>
    /// <param name="mode">The mode of this evaluation.</param>
    /// <returns><see langword="true"/> when the stored verdict is the one this write carries.</returns>
    private static bool IsSameStoredVerdict(JsonObject stored, string gateKey, JsonObject block, GateMode mode) =>
        stored[ModeKeyName] is JsonValue storedMode
        && storedMode.TryGetValue<string>(out var storedModeWire)
        && string.Equals(storedModeWire, mode.ToWire(), StringComparison.Ordinal)
        && stored[gateKey] is { } storedBlock
        && JsonNode.DeepEquals(storedBlock, block);

    /// <summary>
    /// The values of <paramref name="values"/> PostgreSQL can store (02.8.2-T1 SEC-8): a string a <c>jsonb</c> write
    /// would refuse — one carrying a <c>NUL</c> or an unpaired surrogate — is dropped at copy time, so one
    /// unreadable vendor string cannot make the run's verdict, and with it the notice the owner still needs,
    /// unstoreable on every retry.
    /// </summary>
    /// <param name="values">The values as the evidence carries them.</param>
    /// <returns>The storable ones, in order; the same list when every value is storable.</returns>
    private static IReadOnlyList<string> Storable(IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (IsStorableText(values[index]))
            {
                continue;
            }

            var kept = new List<string>(values.Count - 1);

            for (var keptIndex = 0; keptIndex < values.Count; keptIndex++)
            {
                if (keptIndex != index && IsStorableText(values[keptIndex]))
                {
                    kept.Add(values[keptIndex]);
                }
            }

            return kept;
        }

        return values;
    }

    /// <summary>Whether a copied string is exactly what <c>jsonb</c> stores: nothing in it needed cleaning.</summary>
    /// <param name="value">The value to copy.</param>
    /// <returns><see langword="true"/> when the value can be stored as it is.</returns>
    private static bool IsStorableText(string value) =>
        string.Equals(TextSafety.Clean(value), value, StringComparison.Ordinal);

    /// <summary>
    /// A bounded, control-character-free rendering of a stored string for a log property (02.8.2-T1 SEC-6): the
    /// gate profile reference and the profile name come from stored configuration, so a long value, or one carrying
    /// control characters, must not reach a log line as it is.
    /// </summary>
    /// <remarks>
    /// Public because this solution has no <c>InternalsVisibleTo</c> and the tests exercise it (the
    /// <c>BibleEtags</c> precedent); the gate evaluators of this namespace call it too.
    /// </remarks>
    /// <param name="value">The stored value; <see langword="null"/> becomes the empty string.</param>
    /// <returns>At most <see cref="LogSafeMaxLength"/> characters, none below U+0020 and none U+007F.</returns>
    public static string LogSafe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var length = Math.Min(value.Length, LogSafeMaxLength);
        var buffer = new char[length];

        for (var index = 0; index < length; index++)
        {
            var character = value[index];
            buffer[index] = character < ' ' || character == '\u007f' ? '?' : character;
        }

        var safe = new string(buffer);

        // A cut that lands inside a surrogate pair would leave half of one in the log line: drop the orphan.
        return char.IsHighSurrogate(safe[^1]) ? safe[..^1] : safe;
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

    private static ErrorOr<GateWriteResult> Persist(AutopilotRun run, JsonObject document)
    {
        var json = document.ToJsonString();

        // SEC-8 (02.8.2-T1): PostgreSQL's jsonb refuses a NUL and an unpaired surrogate, and the exception it
        // answers at commit would roll back the verdict's whole transaction — including the owner's notice — on
        // every retry. The one point every write path goes through checks the merged document first.
        if (!TextSafety.IsPersistableJson(json))
        {
            return Error.Failure(
                NotPersistableErrorCode,
                "The gate verdict carries a value PostgreSQL cannot store; nothing was written.");
        }

        var recorded = run.RecordGateResults(json);

        return recorded.IsError ? recorded.Errors : GateWriteResult.Written;
    }

    private string Timestamp() => Format(clock.UtcNow);

    private static string Format(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
