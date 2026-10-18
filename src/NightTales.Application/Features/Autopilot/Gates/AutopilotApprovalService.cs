using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Persistence;
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
    /// version is no longer the one the gate evaluated — becomes <see cref="EtagConflict"/>; every other code
    /// becomes <see cref="ApproveFailed"/>. Both halves map through here, so the story and video paths cannot
    /// drift.
    /// </summary>
    /// <param name="error">The error the approve command returned.</param>
    /// <returns>The code the run's verdict is amended with.</returns>
    public static string Of(Error error) =>
        error.Code == ErrorCodes.StoryVersionConflict ? EtagConflict : ApproveFailed;
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
/// <c>autopilot.video_auto_approved</c> describe an approval that actually happened, so they are written only on
/// <see cref="ApprovalOutcome.Approved"/> and never on an idempotent replay. They carry ids, scores and codes
/// only (D13/D19), in a transaction of their own.
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
            LogAlreadyApproved(runId, current.Id, GateResultWriter.StoryGate);

            return ApprovalOutcome.AlreadyApproved;
        }

        if (current.CurrentStoryVersionId is not { } currentVersionId)
        {
            return GateErrors.EpisodeWithoutStoryVersion(current.Id);
        }

        var approved = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(
                new ApproveStoryCommand(
                    current.Id,
                    StoryEtag.For(currentVersionId),
                    Attestation: null,
                    Source: ApprovalSource.Autopilot),
                ct)
            .ConfigureAwait(false);

        if (approved.IsError)
        {
            // The episode was approved for its own current version while this evaluation was in flight: the state
            // refusal means "already done", never "refused", so the run counts as approved.
            if (current.ApprovedStoryVersionId == current.CurrentStoryVersionId
                && approved.Errors.All(error => error.Code == ErrorCodes.EpisodeInvalidState))
            {
                LogAlreadyApproved(runId, current.Id, GateResultWriter.StoryGate);

                return ApprovalOutcome.AlreadyApproved;
            }

            return Refused(runId, GateResultWriter.StoryGate, approved.Errors);
        }

        await WriteAuditAsync(
                scope.ServiceProvider,
                ownerId,
                current.Id,
                StoryApprovedAction,
                StoryData(runId, current.Id, result, evidence),
                ct)
            .ConfigureAwait(false);

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

        // `ready` is past the video review: the render of this run was accepted, so this evaluation is a replay.
        // There is no per-render stamp on the episode to compare with, which is why the state is the whole test —
        // and why a refusal on that same state is read as "already done" below.
        if (current.Status == EpisodeStatus.Ready)
        {
            LogAlreadyApproved(runId, current.Id, GateResultWriter.VideoGate);

            return ApprovalOutcome.AlreadyApproved;
        }

        // `ApproveVideoCommand(episodeId, "autopilot")` passes no `IfMatch`, so the command itself cannot tell
        // that the verdict judges a render the episode has since replaced: the identity check is the guard, and a
        // mismatch is a refusal the owner sees rather than a wrong pass.
        if (current.FinalVideoAssetId != result.AssetId)
        {
            LogStaleVideoVerdict(runId, current.Id, result.AssetId);

            return GateErrors.StaleVideoVerdict(current.Id);
        }

        var approved = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new ApproveVideoCommand(current.Id, "autopilot"), ct)
            .ConfigureAwait(false);

        if (approved.IsError)
        {
            // The 09.7.1-T1 command's `IfMatch` is null for an in-process caller: only the ETag comparison is
            // skipped, never the state check, so a refusal here is a real state refusal unless the episode moved
            // to `ready` under us.
            if (current.Status == EpisodeStatus.Ready
                && approved.Errors.All(error => error.Code == ErrorCodes.EpisodeInvalidState))
            {
                LogAlreadyApproved(runId, current.Id, GateResultWriter.VideoGate);

                return ApprovalOutcome.AlreadyApproved;
            }

            return Refused(runId, GateResultWriter.VideoGate, approved.Errors);
        }

        await WriteAuditAsync(
                scope.ServiceProvider,
                ownerId,
                current.Id,
                VideoApprovedAction,
                VideoData(runId, current.Id, result),
                ct)
            .ConfigureAwait(false);

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

    private static async ValueTask<ErrorOr<Episode>> LoadEpisodeAsync(
        IAppDbContext db,
        Guid runId,
        CancellationToken ct)
    {
        var run = await db.AutopilotRuns
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
            .FirstOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);

        return episode is null ? GateErrors.EpisodeOfRunNotFound(runId) : episode;
    }

    /// <summary>
    /// Writes one approval's audit row in a transaction of its own. The approval has already committed by the
    /// time this runs, so a failure here is logged rather than answered: the episode <em>is</em> approved, and
    /// reporting an error would make the caller record a refusal the owner cannot act on.
    /// </summary>
    /// <param name="provider">The pinned scope of this approval.</param>
    /// <param name="ownerId">The run's owner: actor and subject of the row.</param>
    /// <param name="episodeId">The approved episode.</param>
    /// <param name="action">The audit action key.</param>
    /// <param name="data">The ids/scores/codes projection.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    private async ValueTask WriteAuditAsync(
        IServiceProvider provider,
        Guid ownerId,
        Guid episodeId,
        string action,
        string data,
        CancellationToken ct)
    {
        var unitOfWork = provider.GetRequiredService<IUnitOfWork>();
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
            ["run_id"] = runId,
            ["episode_id"] = episodeId,
            ["attempt"] = result.Attempt,
            ["mode"] = GateMode.Live.ToWire(),
            ["checks"] = CheckProjection(result.Checks),
            ["failures"] = result.Failures,
            ["mean_review_score"] = CheckValue(result.Checks, StoryGateCheckIds.SelfReviewMeanScore),
            ["source_similarity"] = evidence.SourceSimilarity,
            ["past_episode_similarity"] = evidence.PastEpisodeSimilarity,
        });

    /// <summary>The video approval's audit projection: ids and the numbers the checks measured (D13).</summary>
    /// <param name="runId">The run.</param>
    /// <param name="episodeId">The approved episode.</param>
    /// <param name="result">The verdict.</param>
    /// <returns>The serialised <c>data</c> column.</returns>
    private static string VideoData(Guid runId, Guid episodeId, VideoGateResult result) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["run_id"] = runId,
            ["episode_id"] = episodeId,
            ["render_job_id"] = result.RenderJobId,
            ["asset_id"] = result.AssetId,
            ["mode"] = GateMode.Live.ToWire(),
            ["checks"] = CheckProjection(result.Checks),
            ["failures"] = result.Failures,
            ["loudness"] = CheckValue(result.Checks, VideoGateCheckIds.Loudness),
            ["duration_sec"] = CheckValue(result.Checks, VideoGateCheckIds.Duration),
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
}
