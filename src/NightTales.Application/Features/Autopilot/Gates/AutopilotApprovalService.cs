using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Application.Features.VideoReview.Commands.ApproveVideo;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>How one call of <see cref="AutopilotApprovalService"/> ended.</summary>
public enum ApprovalOutcome
{
    /// <summary>The approve command was sent and the domain accepted it.</summary>
    Approved,

    /// <summary>
    /// The episode was already past this review for the same version (or was already <c>ready</c>): a replay of an
    /// approval that already ran, so the caller treats the gate as passed and nothing is written twice
    /// (<c>AutoApproveStory_SecondCall_IsIdempotent</c>).
    /// </summary>
    AlreadyApproved,

    /// <summary>The gate is in dry run, so no command was sent at all (02.8.2-T3).</summary>
    DryRunSkipped,
}

/// <summary>
/// The failure codes an approval records on a run's verdict when the command that would approve refuses
/// (02.8.2 plan §"Failure handling"): the run log then shows why the owner has to approve by hand.
/// </summary>
public static class ApprovalFailureCodes
{
    /// <summary>The approval was refused because the episode moved since the gate read it (the story ETag).</summary>
    public const string EtagConflict = "etag_conflict";

    /// <summary>The approval was refused for any other reason (state, retention, an unexpected error).</summary>
    public const string ApproveFailed = "approve_failed";

    /// <summary>
    /// The failure code of one refused approval: <c>story_version_conflict</c> — the episode's current story
    /// version is no longer the one the gate evaluated — and the video half's <c>episode_conflict</c> (the stale
    /// episode ETag now sent with the approve) become <see cref="EtagConflict"/>; every other code becomes
    /// <see cref="ApproveFailed"/>. Both halves map through here, so the story and video paths cannot drift.
    /// </summary>
    /// <remarks>
    /// A lost audit row is not a refusal and never arrives here: the gates surface
    /// <see cref="GateErrors.ApprovalAuditFailedCode"/> unchanged instead of recording a failure code (02.8.2-T1 R2).
    /// </remarks>
    /// <param name="error">The error the approve command returned.</param>
    /// <returns>The code the run's verdict is amended with.</returns>
    public static string Of(Error error) =>
        error.Code is ErrorCodes.StoryVersionConflict or ErrorCodes.EpisodeConflict
            ? EtagConflict
            : ApproveFailed;
}

/// <summary>
/// Approves what a passing gate verdict allows, through the same commands a user calls (02.8.2 plan §4 UC2/UC4):
/// <c>ApproveStoryCommand</c> and <c>ApproveVideoCommand</c>, sent through <c>ISender</c> as a system actor under
/// <see cref="ISystemContext.For"/> for the run's owner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own scope, pinned by itself.</b> The evaluator calls this from a scope that may already be inside a
/// transaction, and the approval has to commit independently, exactly as the command does when an HTTP request
/// sends it. So this service learns the run's owner through <see cref="ISystemWorkQuery"/> — the one allow-listed
/// cross-user read, which yields ids only — creates a scope, pins it to that owner <em>before</em> any
/// <c>DbContext</c> is resolved, and does all of its work there.
/// </para>
/// <para>
/// <b>Nothing is approved without a pass.</b> A dry run answers <see cref="ApprovalOutcome.DryRunSkipped"/>, and a
/// verdict that did not pass — or that is held for the owner by design — is refused with
/// <see cref="GateErrors.GateNotPassed"/> before any command is built: the caller bug is made un-repeatable,
/// never turned into an episode state.
/// </para>
/// <para>
/// <b>The audit row is written after, not before.</b> <c>autopilot.story_auto_approved</c> and
/// <c>autopilot.video_auto_approved</c> describe an approval that actually happened, so they are written for one
/// and never for an idempotent replay — and a replay whose approval really was the autopilot's backfills the row
/// a lost first attempt may have left missing, never one the owner made (<see cref="ReplayAsync"/>). They carry
/// ids, scores and codes only (D13/D19), in a transaction of their own; and a failure to commit one is answered
/// as an error instead of being swallowed, so the caller retries the call that backfills it.
/// </para>
/// <para>
/// <b>Every approval is bound to what the verdict evaluated.</b> The story command's ETag names
/// <see cref="StoryGateResult.StoryVersionId"/> and the video command's names the episode ETag the identity check
/// read, so an edit, a revision or a re-render between the evaluation and the approval is refused by the
/// command's own guard — recorded as <c>etag_conflict</c> and left to the owner — instead of approving content
/// the gate never read. A non-error answer alone is never read as "approved": the command answers
/// its own idempotent replay with a success too; only the service's own record tells the two apart.
/// </para>
/// </remarks>
/// <param name="systemWork">The id-only cross-user read that yields the run's owner.</param>
/// <param name="scopes">The factory of the scope this service works in.</param>
/// <param name="clock">The clock the audit rows are stamped with.</param>
/// <param name="logger">The ids-only log of this service.</param>
public sealed partial class AutopilotApprovalService(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<AutopilotApprovalService> logger)
{
    /// <summary>The audit action of an autopilot story approval (02 README D13).</summary>
    public const string StoryApprovedAction = "autopilot.story_auto_approved";

    /// <summary>The audit action of an autopilot video approval (02 README D13).</summary>
    public const string VideoApprovedAction = "autopilot.video_auto_approved";

    /// <summary>The audit rows' <c>entity_type</c>.</summary>
    public const string EpisodeEntityType = "episode";

    /// <summary>The audit action <c>ApproveStoryCommand</c>'s own pipeline writes on a new approval.</summary>
    /// <remarks>
    /// The key of that command's <c>[Audited]</c> attribute; kept here as the one string a replay reads to
    /// distinguish its own approval from the owner's (<see cref="IsAutopilotStoryApprovalAsync"/>).
    /// </remarks>
    private const string StoryApprovedCommandAction = "episode.story_approved";

    /// <summary>The <c>approval_source</c> the approve command records for an autopilot approval.</summary>
    private const string AutopilotApprovalSource = "autopilot";

    /// <summary>
    /// How many of an episode's audit rows one replay decision reads: an episode writes one
    /// <c>episode.story_approved</c> row per accepted version and one auto-approval row per autopilot approval, so
    /// the newest window is the whole live set. The <c>data</c> member is matched in memory, exactly as
    /// <c>StoryReadProjection.AcceptedAtAsync</c> reads the attestation rows of the same table.
    /// </summary>
    private const int AuditRowWindow = 20;

    /// <summary>Sends the story approval a passing, live verdict allows.</summary>
    /// <param name="runId">The run whose story passed.</param>
    /// <param name="result">The passing verdict; its story version is what the approval is compared against.</param>
    /// <param name="evidence">The context of that evaluation, for the audit row's similarities.</param>
    /// <param name="mode">The mode of the evaluation; a dry run sends nothing.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The outcome, or the error the command answered unchanged (its code preserved).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="evidence"/> is <see langword="null"/>.</exception>
    public async ValueTask<ErrorOr<ApprovalOutcome>> ApproveStoryAsync(
        Guid runId,
        StoryGateResult result,
        GateStoryEvidence evidence,
        GateMode mode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(evidence);

        if (mode == GateMode.DryRun)
        {
            return ApprovalOutcome.DryRunSkipped;
        }

        if (!result.Pass || result.Hold)
        {
            return GateErrors.GateNotPassed;
        }

        var owner = await ResolveOwnerAsync(runId, ct).ConfigureAwait(false);
        if (owner is not { } ownerId)
        {
            return GateErrors.RunNotFound;
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(ownerId);

        var episode = await LoadEpisodeAsync(scope.ServiceProvider.GetRequiredService<IAppDbContext>(), runId, ct)
            .ConfigureAwait(false);

        if (episode.IsError)
        {
            return episode.Errors;
        }

        var current = episode.Value;

        if (current.ApprovedStoryVersionId == result.StoryVersionId)
        {
            return await ReplayAsync(scope.ServiceProvider, ownerId, runId, current.Id, result, evidence, ct)
                .ConfigureAwait(false);
        }

        if (current.CurrentStoryVersionId is null)
        {
            return GateErrors.EpisodeWithoutStoryVersion(current.Id);
        }

        // The ETag names the version the verdict evaluated, never the episode's current one: an edit, a revision
        // or a regeneration between the evaluation and this call fails the command's own If-Match with
        // `story_version_conflict` — refused and recorded as `etag_conflict` — instead of approving a text the
        // gate never read.
        var approved = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(
                new ApproveStoryCommand(
                    current.Id,
                    StoryEtag.For(result.StoryVersionId),
                    Attestation: null,
                    Source: ApprovalSource.Autopilot),
                ct)
            .ConfigureAwait(false);

        if (approved.IsError)
        {
            // A refusal here is the command's own guard, and only that: the replay of this verdict's own version
            // was answered before the command was sent, so an episode that was approved for a *different* version
            // (or any other state the command refuses) never counts as "already done" — answering it as a pass
            // would report an approval for a version nobody approved.
            return Refused(runId, GateResultWriter.StoryGate, approved.Errors);
        }

        // A non-error answer is not proof that this call approved anything: the command answers its own stage-1
        // replay with a success when the owner approved the very version the ETag names in the window between the
        // read above and the Send. The command's own `episode.story_approved` row — written inside its
        // transaction, with the source it approved under — is the one thing that tells the two apart.
        if (!await IsAutopilotStoryApprovalAsync(
                scope.ServiceProvider.GetRequiredService<IReadDb>(),
                ownerId,
                current.Id,
                result.StoryVersionId,
                ct)
            .ConfigureAwait(false))
        {
            LogAlreadyApproved(runId, current.Id, GateResultWriter.StoryGate);

            return ApprovalOutcome.AlreadyApproved;
        }

        var written = await WriteAuditAsync(
                scope.ServiceProvider,
                ownerId,
                current.Id,
                StoryApprovedAction,
                StoryData(runId, current.Id, result, evidence),
                ct)
            .ConfigureAwait(false);

        if (written.IsError)
        {
            return written.Errors;
        }

        LogApproved(runId, current.Id, GateResultWriter.StoryGate, result.Attempt);

        return ApprovalOutcome.Approved;
    }

    /// <summary>Sends the video approval a passing, live verdict allows.</summary>
    /// <param name="runId">The run whose render passed.</param>
    /// <param name="result">The passing verdict; its render job and asset are what the audit row names.</param>
    /// <param name="mode">The mode of the evaluation; a dry run sends nothing.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The outcome, or the error the command answered unchanged (its code preserved).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public async ValueTask<ErrorOr<ApprovalOutcome>> ApproveVideoAsync(
        Guid runId,
        VideoGateResult result,
        GateMode mode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (mode == GateMode.DryRun)
        {
            return ApprovalOutcome.DryRunSkipped;
        }

        if (!result.Pass)
        {
            return GateErrors.GateNotPassed;
        }

        var owner = await ResolveOwnerAsync(runId, ct).ConfigureAwait(false);
        if (owner is not { } ownerId)
        {
            return GateErrors.RunNotFound;
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(ownerId);

        var episode = await LoadEpisodeAsync(scope.ServiceProvider.GetRequiredService<IAppDbContext>(), runId, ct)
            .ConfigureAwait(false);

        if (episode.IsError)
        {
            return episode.Errors;
        }

        var current = episode.Value;

        // The identity guard runs first: `ready` is past the video review only when this verdict's asset is
        // the asset the episode accepted. A late or duplicate verdict for a render the episode has since
        // replaced must be refused, never counted as an approval.
        if (current.FinalVideoAssetId != result.AssetId)
        {
            LogStaleVideoVerdict(runId, current.Id, result.AssetId);

            return GateErrors.StaleVideoVerdict(current.Id);
        }

        if (current.Status == EpisodeStatus.Ready)
        {
            // A replay, and nothing is backfilled: the video approve command's own row
            // (`episode.video_approved`) records neither an approval source nor the asset, so an acceptance the
            // owner made cannot be told from this service's. Writing `autopilot.video_auto_approved` here would
            // claim an autopilot approval the run may never have made.
            LogAlreadyApproved(runId, current.Id, GateResultWriter.VideoGate);

            return ApprovalOutcome.AlreadyApproved;
        }

        // The `If-Match` is the episode ETag of the snapshot read above: the command compares it under its own
        // row lock, so a re-render — or any other episode write — between the identity check and that lock is
        // refused (`episode_conflict`, recorded as `etag_conflict`) instead of approving a render the episode no
        // longer holds. Without it the command's own null-If-Match path skips the comparison entirely.
        var approved = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new ApproveVideoCommand(current.Id, "autopilot", ETags.Episode(current.Version)), ct)
            .ConfigureAwait(false);

        if (approved.IsError)
        {
            // No "already done" reading is left at this point: an episode already `ready` on this asset returned
            // above, so a refusal is the command's own guard — a state it no longer accepts, or the stale tag —
            // and is recorded for the owner. The one exception is the owner having accepted this very cut while
            // the command ran: a fresh read is what tells that apart from a real rejection.
            if (approved.Errors.All(error => error is { Code: ErrorCodes.EpisodeInvalidState or ErrorCodes.EpisodeConflict })
                && await HasAcceptedRenderAsync(
                        scope.ServiceProvider.GetRequiredService<IAppDbContext>(), current.Id, result.AssetId, ct)
                    .ConfigureAwait(false))
            {
                LogAlreadyApproved(runId, current.Id, GateResultWriter.VideoGate);

                return ApprovalOutcome.AlreadyApproved;
            }

            return Refused(runId, GateResultWriter.VideoGate, approved.Errors);
        }

        var written = await WriteAuditAsync(
                scope.ServiceProvider,
                ownerId,
                current.Id,
                VideoApprovedAction,
                VideoData(runId, current.Id, result),
                ct)
            .ConfigureAwait(false);

        if (written.IsError)
        {
            return written.Errors;
        }

        LogApproved(runId, current.Id, GateResultWriter.VideoGate, attempt: 0);

        return ApprovalOutcome.Approved;
    }

    /// <summary>The owner of one run, read without crossing the tenant boundary in any other way.</summary>
    /// <param name="runId">The run to resolve.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The owner's id, or <see langword="null"/> when no such run exists.</returns>
    private async ValueTask<Guid?> ResolveOwnerAsync(Guid runId, CancellationToken ct)
    {
        Guid? owner = null;

        await foreach (var (_, userId) in systemWork
            .PendingAsync<AutopilotRun>(run => run.Id == runId, 1, ct)
            .ConfigureAwait(false))
        {
            owner = userId;
        }

        return owner;
    }

    /// <summary>
    /// The run and the episode of one approval, read without tracking: the approval only reads them, and the
    /// commands it sends do their own tracked, row-locked read — a tracked snapshot here would be the one the
    /// handler's identity resolution returns, hiding what the owner did while this call ran.
    /// </summary>
    /// <param name="db">The pinned scope's write model.</param>
    /// <param name="runId">The run whose episode is wanted.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The episode, or the error naming what is missing.</returns>
    private static async ValueTask<ErrorOr<Episode>> LoadEpisodeAsync(
        IAppDbContext db,
        Guid runId,
        CancellationToken ct)
    {
        var run = await db.AutopilotRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, ct)
            .ConfigureAwait(false);

        if (run is null)
        {
            return GateErrors.RunNotFound;
        }

        if (run.EpisodeId is not { } episodeId)
        {
            return GateErrors.RunHasNoEpisode(runId);
        }

        var episode = await db.Episodes
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);

        return episode is null ? GateErrors.EpisodeOfRunNotFound(runId) : episode;
    }

    /// <summary>
    /// A replay of an approval that already ran for this verdict's story version: it answers
    /// <see cref="ApprovalOutcome.AlreadyApproved"/> and writes nothing twice — except the one row a lost first
    /// attempt may be missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The approval commits inside the command's own transaction, while <c>autopilot.story_auto_approved</c> is
    /// written afterwards in a unit of work of its own: a crash, a refused commit or a killed worker between the
    /// two loses the row for good, because every later attempt is this replay. So a replay whose approval really
    /// was the autopilot's — the command's own row for that version carries the autopilot source — backfills it
    /// once: the existence check runs first and the row is written inside one unit of work.
    /// </para>
    /// <para>
    /// An approval the owner made is never backfilled: the row says an <em>autopilot</em> approval, and the
    /// owner's own row is the record of theirs (plan §4 UC4, the packet's D13 rule). Two simultaneous replays of
    /// one run can both find the row missing and both write it; a run's ticks are serialised by the orchestrator
    /// (02.8.3-T1), and a duplicated row is a duplicated fact, never a wrong one.
    /// </para>
    /// </remarks>
    /// <param name="provider">The pinned scope of this approval.</param>
    /// <param name="ownerId">The run's owner: actor and subject of the row.</param>
    /// <param name="runId">The run whose approval is replayed.</param>
    /// <param name="episodeId">The approved episode.</param>
    /// <param name="result">The verdict the replay belongs to, for the backfilled row's projection.</param>
    /// <param name="evidence">The evaluation's context, for the backfilled row's similarities.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns><see cref="ApprovalOutcome.AlreadyApproved"/>, or the error a refused backfill answered.</returns>
    private async ValueTask<ErrorOr<ApprovalOutcome>> ReplayAsync(
        IServiceProvider provider,
        Guid ownerId,
        Guid runId,
        Guid episodeId,
        StoryGateResult result,
        GateStoryEvidence evidence,
        CancellationToken ct)
    {
        var readDb = provider.GetRequiredService<IReadDb>();

        if (await IsAutopilotStoryApprovalAsync(readDb, ownerId, episodeId, result.StoryVersionId, ct)
                .ConfigureAwait(false)
            && !await HasAutoApprovalRowAsync(readDb, ownerId, episodeId, runId, ct).ConfigureAwait(false))
        {
            var backfilled = await WriteAuditAsync(
                    provider,
                    ownerId,
                    episodeId,
                    StoryApprovedAction,
                    StoryData(runId, episodeId, result, evidence),
                    ct)
                .ConfigureAwait(false);

            if (backfilled.IsError)
            {
                return backfilled.Errors;
            }

            LogAuditBackfilled(runId, episodeId);
        }

        LogAlreadyApproved(runId, episodeId, GateResultWriter.StoryGate);

        return ApprovalOutcome.AlreadyApproved;
    }

    /// <summary>
    /// Whether an approval of <paramref name="storyVersionId"/> by the autopilot actor is on record for the
    /// episode: the one committed fact that separates an approval this service made from one the owner made in the
    /// window between the evaluation and the command.
    /// </summary>
    /// <remarks>
    /// The approve command's own pipeline writes <c>episode.story_approved</c> inside its transaction and only for
    /// a new approval — the stage-1 replay returns before that write — and its <c>data</c> carries the version it
    /// approved and the source it approved under (<c>ApproveStoryCommand.ToAuditData</c>).
    /// </remarks>
    /// <param name="readDb">The read model of the pinned scope.</param>
    /// <param name="ownerId">The run's owner, part of the predicate.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="storyVersionId">The version the verdict evaluated.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when the autopilot approval of that version is committed.</returns>
    private static async ValueTask<bool> IsAutopilotStoryApprovalAsync(
        IReadDb readDb,
        Guid ownerId,
        Guid episodeId,
        Guid storyVersionId,
        CancellationToken ct)
    {
        var rows = await AuditRowsAsync(readDb, ownerId, episodeId, StoryApprovedCommandAction, ct)
            .ConfigureAwait(false);

        foreach (var data in rows)
        {
            if (IsApprovalOf(data, storyVersionId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this run's own auto-approval row is already committed for the episode.</summary>
    /// <param name="readDb">The read model of the pinned scope.</param>
    /// <param name="ownerId">The run's owner, part of the predicate.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="runId">The run whose row is looked for.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when the row exists, so nothing is backfilled.</returns>
    private static async ValueTask<bool> HasAutoApprovalRowAsync(
        IReadDb readDb,
        Guid ownerId,
        Guid episodeId,
        Guid runId,
        CancellationToken ct)
    {
        var rows = await AuditRowsAsync(readDb, ownerId, episodeId, StoryApprovedAction, ct).ConfigureAwait(false);

        foreach (var data in rows)
        {
            if (IsRunOf(data, runId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The newest rows of one audit action for one episode, as their stored <c>data</c> payloads.
    /// </summary>
    /// <remarks>
    /// <c>audit_logs</c> is not <see cref="IUserOwned"/> and carries no tenant filter, so the episode's owner is
    /// part of the predicate — never a read across users, the rule <c>StoryReadProjection</c> already follows for
    /// this table. The window bounds what is loaded; the <c>data</c> member is matched in memory, because a jsonb
    /// predicate does not translate.
    /// </remarks>
    /// <param name="readDb">The read model of the pinned scope.</param>
    /// <param name="ownerId">The run's owner, part of the predicate.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="action">The audit action to read.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The payloads, newest first.</returns>
    private static async ValueTask<List<string>> AuditRowsAsync(
        IReadDb readDb,
        Guid ownerId,
        Guid episodeId,
        string action,
        CancellationToken ct) =>
        await readDb.Query<AuditLog>()
            .AsNoTracking()
            .Where(row => row.EntityType == EpisodeEntityType
                && row.EntityId == episodeId
                && row.SubjectUserId == ownerId
                && row.Action == action)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(AuditRowWindow)
            .Select(row => row.DataJson)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>One approval row's own projection: the version it approved, and the source it approved under.</summary>
    /// <param name="dataJson">The row's stored <c>data</c>.</param>
    /// <param name="storyVersionId">The version the verdict evaluated.</param>
    /// <returns><see langword="true"/> only for an autopilot approval of that exact version.</returns>
    private static bool IsApprovalOf(string dataJson, Guid storyVersionId) =>
        ReadMember(dataJson, "story_version_id") is { } stored
        && Guid.TryParse(stored, out var versionId)
        && versionId == storyVersionId
        && string.Equals(
            ReadMember(dataJson, "approval_source"),
            AutopilotApprovalSource,
            StringComparison.Ordinal);

    /// <summary>Whether one auto-approval row's projection names this run.</summary>
    /// <param name="dataJson">The row's stored <c>data</c>.</param>
    /// <param name="runId">The run.</param>
    /// <returns><see langword="true"/> when the row is this run's own approval.</returns>
    private static bool IsRunOf(string dataJson, Guid runId) =>
        ReadMember(dataJson, "runId") is { } stored
        && Guid.TryParse(stored, out var parsed)
        && parsed == runId;

    /// <summary>
    /// One string member of a stored <c>data</c> payload: <see langword="null"/> when the payload is not an object,
    /// is not JSON at all, or names neither the member nor a string.
    /// </summary>
    /// <param name="dataJson">The row's stored <c>data</c>.</param>
    /// <param name="member">The member to read.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static string? ReadMember(string dataJson, string member)
    {
        if (ParseObject(dataJson) is not { } data)
        {
            return null;
        }

        return data[member] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>One stored payload as a JSON object, or <see langword="null"/> when it is unreadable.</summary>
    /// <param name="json">The stored text.</param>
    /// <returns>The object, or <see langword="null"/>.</returns>
    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the episode has since accepted exactly this render — a fresh read, never the snapshot this call
    /// already judged (the video replay after a concurrent owner approval).
    /// </summary>
    /// <param name="db">The pinned scope's write model.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="assetId">The asset the verdict evaluated.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when the episode is <c>ready</c> on that asset.</returns>
    private static async ValueTask<bool> HasAcceptedRenderAsync(
        IAppDbContext db,
        Guid episodeId,
        Guid assetId,
        CancellationToken ct)
    {
        var episode = await db.Episodes
            .AsNoTracking()
            .Where(candidate => candidate.Id == episodeId)
            .Select(candidate => new { candidate.Status, candidate.FinalVideoAssetId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return episode is { Status: EpisodeStatus.Ready, FinalVideoAssetId: { } accepted } && accepted == assetId;
    }

    /// <summary>
    /// Writes one approval's audit row in a transaction of its own and answers whether it committed.
    /// </summary>
    /// <remarks>
    /// The approval has already committed by the time this runs, so the row cannot join the command's transaction;
    /// and a refusal or a crash here loses the record of an approval that did happen. The caller therefore gets
    /// the failure back instead of having it logged away: a commit the database refuses, and an exception from
    /// the write itself, are both answered as an error the caller retries — and the retry is the replay path that
    /// backfills the missing row. An abandoned transaction is cleaned up by the scope's disposal, which is the
    /// rollback <c>UnitOfWorkBehavior</c> already relies on.
    /// </remarks>
    /// <param name="provider">The pinned scope of this approval.</param>
    /// <param name="ownerId">The run's owner: actor and subject of the row.</param>
    /// <param name="episodeId">The approved episode.</param>
    /// <param name="action">The audit action key.</param>
    /// <param name="data">The ids/scores/codes projection.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns><c>Success</c> once the row is committed, or the error that stopped the write.</returns>
    private async ValueTask<ErrorOr<Success>> WriteAuditAsync(
        IServiceProvider provider,
        Guid ownerId,
        Guid episodeId,
        string action,
        string data,
        CancellationToken ct)
    {
        var unitOfWork = provider.GetRequiredService<IUnitOfWork>();

        try
        {
            await unitOfWork.BeginAsync(ct).ConfigureAwait(false);

            await provider
                .GetRequiredService<IAuditWriter>()
                .WriteAsync(
                    new AuditEntry(
                        ActorUserId: ownerId,
                        ActorType: ActorType.System,
                        SubjectUserId: ownerId,
                        Action: action,
                        Category: Category.Episodes,
                        Visibility: Visibility.User,
                        EntityType: EpisodeEntityType,
                        EntityId: episodeId,
                        DataJson: data,
                        Ip: null,
                        UserAgent: null,
                        ActorLabel: null,
                        OccurredAt: clock.UtcNow),
                    ct)
                .ConfigureAwait(false);

            var committed = await unitOfWork.CommitAsync(ct).ConfigureAwait(false);
            if (committed.IsError)
            {
                LogAuditFailed(episodeId, action, committed.FirstError.Code);

                return committed.Errors;
            }

            return Result.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAuditThrew(episodeId, action);

            return GateErrors.ApprovalAuditFailed(episodeId, action);
        }
    }

    /// <summary>The story approval's audit projection: ids, scores and codes only (D13).</summary>
    /// <param name="runId">The run.</param>
    /// <param name="episodeId">The approved episode.</param>
    /// <param name="result">The verdict.</param>
    /// <param name="evidence">The context of the evaluation.</param>
    /// <returns>The serialised <c>data</c> column.</returns>
    private static string StoryData(
        Guid runId,
        Guid episodeId,
        StoryGateResult result,
        GateStoryEvidence evidence) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runId"] = runId,
            ["episodeId"] = episodeId,
            ["attempt"] = result.Attempt,
            ["mode"] = GateMode.Live.ToWire(),
            ["checks"] = CheckProjection(result.Checks),
            ["failures"] = result.Failures,
            ["meanReviewScore"] = CheckValue(result.Checks, StoryGateCheckIds.SelfReviewMeanScore),
            ["sourceSimilarity"] = evidence.SourceSimilarity,
            ["pastEpisodeSimilarity"] = evidence.PastEpisodeSimilarity,
        });

    /// <summary>The video approval's audit projection: ids and the numbers the checks measured (D13).</summary>
    /// <param name="runId">The run.</param>
    /// <param name="episodeId">The approved episode.</param>
    /// <param name="result">The verdict.</param>
    /// <returns>The serialised <c>data</c> column.</returns>
    private static string VideoData(Guid runId, Guid episodeId, VideoGateResult result) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runId"] = runId,
            ["episodeId"] = episodeId,
            ["renderJobId"] = result.RenderJobId,
            ["assetId"] = result.AssetId,
            ["mode"] = GateMode.Live.ToWire(),
            ["checks"] = CheckProjection(result.Checks),
            ["failures"] = result.Failures,
            ["loudness"] = CheckValue(result.Checks, VideoGateCheckIds.Loudness),
            ["durationSec"] = CheckValue(result.Checks, VideoGateCheckIds.Duration),
        });

    private static List<Dictionary<string, object?>> CheckProjection(IReadOnlyList<GateCheck> checks)
    {
        var projection = new List<Dictionary<string, object?>>(checks.Count);

        foreach (var check in checks)
        {
            projection.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = check.Id,
                ["pass"] = check.Pass,
            });
        }

        return projection;
    }

    private static decimal? CheckValue(IReadOnlyList<GateCheck> checks, string id)
    {
        foreach (var check in checks)
        {
            if (check.Id == id)
            {
                return check.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// The errors of one refused approval, unchanged, so the caller records the failure code and asks the owner by
    /// hand.
    /// </summary>
    /// <remarks>
    /// <b>The stored verdict keeps <c>pass: true</c>.</b> A refusal is not a failed check: the run log's
    /// <c>pass</c> member is the check list's own verdict, and an approval refusal lands in the same block's
    /// <c>failures[]</c> (<see cref="GateResultWriter.AppendFailureAsync"/>, plan §3.4 "Failure handling"). So a
    /// stored <c>pass: true</c> beside a non-empty <c>failures[]</c> reads "the checks passed; the approval
    /// afterwards did not", and a reader must never take <c>pass</c> to mean the episode is approved.
    /// </remarks>
    /// <param name="runId">The run whose approval was refused.</param>
    /// <param name="gate">The gate whose verdict carries the refusal.</param>
    /// <param name="errors">The command's own errors.</param>
    /// <returns>The same errors.</returns>
    private List<Error> Refused(Guid runId, string gate, List<Error> errors)
    {
        LogRefused(runId, gate, errors[0].Code);

        return errors;
    }

    [LoggerMessage(
        EventId = 6454,
        Level = LogLevel.Warning,
        Message = "gate_video_verdict_stale runId={RunId} episodeId={EpisodeId} assetId={AssetId}")]
    private partial void LogStaleVideoVerdict(Guid runId, Guid episodeId, Guid assetId);

    [LoggerMessage(
        EventId = 6450,
        Level = LogLevel.Information,
        Message = "gate_auto_approved runId={RunId} episodeId={EpisodeId} gate={Gate} attempt={Attempt}")]
    private partial void LogApproved(Guid runId, Guid episodeId, string gate, int attempt);

    [LoggerMessage(
        EventId = 6451,
        Level = LogLevel.Information,
        Message = "gate_approval_replay runId={RunId} episodeId={EpisodeId} gate={Gate}")]
    private partial void LogAlreadyApproved(Guid runId, Guid episodeId, string gate);

    [LoggerMessage(
        EventId = 6452,
        Level = LogLevel.Warning,
        Message = "gate_approval_refused runId={RunId} gate={Gate} code={Code}")]
    private partial void LogRefused(Guid runId, string gate, string code);

    [LoggerMessage(
        EventId = 6453,
        Level = LogLevel.Warning,
        Message = "gate_approval_audit_failed episodeId={EpisodeId} action={Action} code={Code}")]
    private partial void LogAuditFailed(Guid episodeId, string action, string code);

    [LoggerMessage(
        EventId = 6436,
        Level = LogLevel.Warning,
        Message = "gate_approval_audit_threw episodeId={EpisodeId} action={Action}")]
    private partial void LogAuditThrew(Guid episodeId, string action);

    [LoggerMessage(
        EventId = 6437,
        Level = LogLevel.Information,
        Message = "gate_approval_audit_backfilled runId={RunId} episodeId={EpisodeId}")]
    private partial void LogAuditBackfilled(Guid runId, Guid episodeId);
}
