using System.Text.Json;
using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Domain.Stories.Generation;

/// <summary>
/// One paid generation of any kind (02 D2, <c>generation_runs</c>): the row's own lifecycle, shared by the five
/// <see cref="GenerationKind"/>s. It holds state only and raises <b>no</b> domain events — the episode is the
/// observable state machine (<c>Episode.Story.cs</c>) and <c>EpisodeUpdated</c> is raised from there (CR-7).
/// </summary>
/// <remarks>
/// <para>
/// The methods are the transitions of plan 02.2.2 §3.4. A repeat that already reached the target (a second
/// <see cref="Fail"/> after a failure, a second <see cref="Start"/> of a running run) is a no-op, because the
/// at-least-once outbox (09.2.4) and the sweeper may deliver the same step twice; a transition that contradicts
/// the current status throws <see cref="InvalidOperationException"/>, since callers check the status first and a
/// wrong one is a programming error.
/// </para>
/// <para>
/// It is an <see cref="Entity"/>, not an aggregate root: it carries no events and no <c>xmin</c> token — the
/// job and the sweeper are idempotent on the terminal status instead.
/// </para>
/// </remarks>
public sealed class GenerationRun : Entity, IUserOwned, IAuditable
{
    private GenerationRun()
    {
    }

    /// <summary>The owner; set from the loaded episode/series, never from a request body (09.2.3).</summary>
    public Guid UserId { get; private set; }

    /// <summary>The series; NOT NULL on every row (TL ruling #2).</summary>
    public Guid SeriesId { get; private set; }

    /// <summary>The episode; null only for the series-level kinds <c>ideas</c> and <c>bible_proposal</c> (TL T17).</summary>
    public Guid? EpisodeId { get; private set; }

    /// <summary>What this run generates.</summary>
    public GenerationKind Kind { get; private set; }

    /// <summary>Who asked for the run.</summary>
    public GenerationTrigger Trigger { get; private set; }

    /// <summary>The idempotency key that created the run; <c>idempotency_keys</c> stays the dedupe authority.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Where the run is in its lifecycle.</summary>
    public GenerationRunStatus Status { get; private set; }

    /// <summary>The D21 stage; forward-only, see <see cref="AdvanceStage"/>.</summary>
    public GenerationStage Stage { get; private set; }

    /// <summary>The F04 return target (<c>draft</c> or <c>story_review</c>); null for kinds that do not move the episode.</summary>
    public EpisodeStatus? PreviousEpisodeStatus { get; private set; }

    /// <summary>The approved fact sheet the handler saw; compared again at job start.</summary>
    public Guid? InputFactSheetId { get; private set; }

    /// <summary>The <c>ReservationId</c> of <c>ICreditLedger.ReserveAsync</c> (D1).</summary>
    public Guid? ReservationId { get; private set; }

    /// <summary>Whether the run counts toward the FR-02-7 daily limit (plan §3.6).</summary>
    public bool CountsTowardLimit { get; private set; }

    /// <summary>The worst-case cost estimate in USD; the budget guard's open bound.</summary>
    public decimal WorstCaseUsd { get; private set; }

    /// <summary>The settled cost in USD, written at settle.</summary>
    public decimal? CostUsd { get; private set; }

    /// <summary>The D8 job-outcome code of a failed run; never an HTTP code.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>Safe text about the outcome (<c>fact_sheet_outdated</c>, <c>stale_run_sweep</c>, …), never content.</summary>
    public string? ErrorDetail { get; private set; }

    /// <summary>Why the queued run is held (TL T19); null unless <see cref="MarkPaused"/> set it.</summary>
    public GenerationPauseReason? PauseReason { get; private set; }

    /// <summary>The story version the run saved.</summary>
    public Guid? ResultStoryVersionId { get; private set; }

    /// <summary>The fact sheet the run saved.</summary>
    public Guid? ResultFactSheetId { get; private set; }

    /// <summary>The D5 composite prompt version, written after the first successful assembly.</summary>
    public string? PromptVersion { get; private set; }

    /// <summary>The per-step model record, a JSON document (<c>jsonb</c>).</summary>
    public string? Models { get; private set; }

    /// <summary>The writer invalid-output retry counter; 0 is the first attempt.</summary>
    public int Attempt { get; private set; }

    /// <summary>When the run was queued; equal to <see cref="CreatedAt"/> by construction.</summary>
    public DateTimeOffset QueuedAt { get; private set; }

    /// <summary>When the job started the run.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>When the run reached a terminal status.</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Queues a run in <see cref="GenerationRunStatus.Queued"/> / <see cref="GenerationStage.Queued"/> with
    /// <see cref="CountsTowardLimit"/> at <paramref name="countsTowardLimit"/> (true unless the kind never
    /// counts, 02.5.3-T2); the caller also lowers it through <see cref="Fail"/> or <see cref="MarkPaused"/>
    /// when the outcome says so.
    /// </summary>
    /// <param name="userId">The owner, taken from the loaded episode/series.</param>
    /// <param name="seriesId">The series; required for every kind.</param>
    /// <param name="episodeId">The episode; required for <c>story</c>, <c>fact_sheet</c>, <c>find_sources</c>, null for <c>ideas</c>, <c>bible_proposal</c>.</param>
    /// <param name="kind">What the run generates.</param>
    /// <param name="trigger">Who asked.</param>
    /// <param name="idempotencyKey">The key that created the run, when there is one.</param>
    /// <param name="previousEpisodeStatus">The status to return the episode to on failure: <c>draft</c> or <c>story_review</c>, or null.</param>
    /// <param name="inputFactSheetId">The fact sheet the handler saw, when the kind uses one.</param>
    /// <param name="reservationId">The credit reservation, when one was made.</param>
    /// <param name="worstCaseUsd">The non-negative worst-case cost in USD.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <param name="countsTowardLimit">
    /// Whether a run of this kind counts toward FR-02-7. <see langword="true"/> for every run the user's own
    /// request is served by; a <c>fact_sheet</c> run is created <see langword="false"/>, because D2 counts
    /// <c>kind = story</c> only (02.5.3-T2).
    /// </param>
    /// <returns>The new run, or a validation error when the scope, the return target or the cost is invalid.</returns>
    public static ErrorOr<GenerationRun> Queue(
        Guid userId,
        Guid seriesId,
        Guid? episodeId,
        GenerationKind kind,
        GenerationTrigger trigger,
        string? idempotencyKey,
        EpisodeStatus? previousEpisodeStatus,
        Guid? inputFactSheetId,
        Guid? reservationId,
        decimal worstCaseUsd,
        DateTimeOffset now,
        bool countsTowardLimit = true)
    {
        if (userId == Guid.Empty || seriesId == Guid.Empty)
        {
            return Error.Validation("generation_run_owner_required", "A generation run needs a user and a series.");
        }

        var seriesLevel = kind is GenerationKind.Ideas or GenerationKind.BibleProposal;
        if (seriesLevel != (episodeId is null))
        {
            return Error.Validation(
                "generation_run_scope_invalid",
                "Only the ideas and bible_proposal kinds are series-level; every other kind needs an episode.");
        }

        if (previousEpisodeStatus is not (null or EpisodeStatus.Draft or EpisodeStatus.StoryReview))
        {
            return Error.Validation(
                "generation_run_previous_status_invalid",
                "The status an episode returns to can only be draft or story_review.");
        }

        if (worstCaseUsd < 0m)
        {
            return Error.Validation("generation_run_cost_invalid", "The worst-case cost cannot be negative.");
        }

        return new GenerationRun
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            SeriesId = seriesId,
            EpisodeId = episodeId,
            Kind = kind,
            Trigger = trigger,
            IdempotencyKey = idempotencyKey,
            Status = GenerationRunStatus.Queued,
            Stage = GenerationStage.Queued,
            PreviousEpisodeStatus = previousEpisodeStatus,
            InputFactSheetId = inputFactSheetId,
            ReservationId = reservationId,
            CountsTowardLimit = countsTowardLimit,
            WorstCaseUsd = worstCaseUsd,
            QueuedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Starts a queued run (<c>queued → running</c>) and clears any pause; a run that already runs stays as it is.
    /// Clearing a pause restores <see cref="CountsTowardLimit"/>, because a run that was held and then served is a
    /// normal run again (plan §3.6: a saved version counts; D2: it stays true for succeeded).
    /// </summary>
    /// <param name="now">The job's clock reading, stored as <see cref="StartedAt"/>.</param>
    /// <exception cref="InvalidOperationException">The run is already finished.</exception>
    public void Start(DateTimeOffset now)
    {
        if (Status == GenerationRunStatus.Running)
        {
            return;
        }

        RequireStatus(GenerationRunStatus.Queued, nameof(Start));

        if (PauseReason is not null)
        {
            CountsTowardLimit = true;
        }

        Status = GenerationRunStatus.Running;
        StartedAt = now;
        PauseReason = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Moves the run to <paramref name="next"/>. Forward-only along the <see cref="GenerationStage"/> order; a
    /// repeat of the current stage succeeds without a change, and the D21 re-entry
    /// <see cref="GenerationStage.Revising"/> → <see cref="GenerationStage.Checking"/> (the revised draft is
    /// checked and reviewed again) is the only way back.
    /// </summary>
    /// <param name="next">The stage to enter.</param>
    /// <param name="now">The job's clock reading.</param>
    /// <returns><c>Success</c>, or a conflict when the run is not running or the stage would go backwards.</returns>
    public ErrorOr<Success> AdvanceStage(GenerationStage next, DateTimeOffset now)
    {
        if (Status != GenerationRunStatus.Running)
        {
            return Error.Conflict("generation_run_not_running", "Only a running generation run changes stage.");
        }

        if (next == Stage)
        {
            return Result.Success;
        }

        var reEntry = Stage == GenerationStage.Revising && next == GenerationStage.Checking;
        if (next < Stage && !reEntry)
        {
            return Error.Conflict(
                "generation_run_stage_backwards",
                $"A generation run cannot go from {Stage} back to {next}.");
        }

        Stage = next;
        UpdatedAt = now;
        return Result.Success;
    }

    /// <summary>
    /// Holds a run (TL T19): it ends up <see cref="GenerationRunStatus.Queued"/>, records why and stops counting
    /// toward the daily limit, because the user's request was not served. A <i>running</i> run is held too (plan
    /// §7.2: <c>ProviderFundsExhausted</c> / <c>IBudgetGuard</c> denial at any step "stays <c>queued</c>"); it goes
    /// back to <see cref="GenerationStage.Queued"/> with no <see cref="StartedAt"/>, so the resume's
    /// <see cref="Start"/> begins it afresh.
    /// </summary>
    /// <param name="reason">Why the run is held.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <exception cref="InvalidOperationException">The run is already finished.</exception>
    public void MarkPaused(GenerationPauseReason reason, DateTimeOffset now)
    {
        RequireOpen(nameof(MarkPaused));

        Status = GenerationRunStatus.Queued;
        Stage = GenerationStage.Queued;
        StartedAt = null;
        PauseReason = reason;
        CountsTowardLimit = false;
        UpdatedAt = now;
    }

    /// <summary>
    /// Records the D5 composite prompt version and the per-step model record once the writer prompt was assembled
    /// (D21); a later call replaces them (a revision step adds its model).
    /// </summary>
    /// <param name="promptVersion">The composite prompt version.</param>
    /// <param name="modelsJson">The per-step model record, a JSON document.</param>
    /// <param name="now">The job's clock reading.</param>
    /// <exception cref="ArgumentException">A value is blank or <paramref name="modelsJson"/> is not valid JSON.</exception>
    /// <exception cref="InvalidOperationException">The run is not running.</exception>
    public void RecordProvenance(string promptVersion, string modelsJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsJson);

        try
        {
            using var document = JsonDocument.Parse(modelsJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The model record must be a JSON document.", nameof(modelsJson), exception);
        }

        RequireStatus(GenerationRunStatus.Running, nameof(RecordProvenance));

        PromptVersion = promptVersion;
        Models = modelsJson;
        UpdatedAt = now;
    }

    /// <summary>Counts one writer invalid-output retry (plan §7.2: <c>attempt + 1</c>); 0 is the first attempt.</summary>
    /// <param name="now">The job's clock reading.</param>
    /// <exception cref="InvalidOperationException">The run is not running.</exception>
    public void RecordAttempt(DateTimeOffset now)
    {
        RequireStatus(GenerationRunStatus.Running, nameof(RecordAttempt));

        Attempt++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Finishes a running run successfully: <c>succeeded</c>, stage <c>done</c>, the settled cost and the saved
    /// result. A <c>story</c> run must name its story version and a <c>fact_sheet</c> run its fact sheet; the other
    /// kinds name none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The served run counts toward FR-02-7 when it belongs to an episode, which is the run the user's own request
    /// was served by (plan §3.6: a saved version counts).
    /// </para>
    /// <para>
    /// A <c>bible_proposal</c> run is the exception (02.6.3-T2, task AC and README D2: only <c>kind = story</c>
    /// counts): it is a nice-to-have the approval bought, not the user's own generation request, so it is created
    /// <c>counts_toward_limit = false</c> and stays that way however it ends — succeeded included. Its
    /// <c>episode_id</c> is NULL (a series-level kind), so no allowance is attributed to it either way.
    /// </para>
    /// </remarks>
    /// <param name="costUsd">The non-negative settled cost in USD.</param>
    /// <param name="resultStoryVersionId">The story version the run saved (required for <c>story</c>).</param>
    /// <param name="resultFactSheetId">The fact sheet the run saved (required for <c>fact_sheet</c>).</param>
    /// <param name="now">The job's clock reading, stored as <see cref="FinishedAt"/>.</param>
    /// <exception cref="InvalidOperationException">The run is not running (and did not already succeed).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="costUsd"/> is negative.</exception>
    /// <exception cref="ArgumentException">A result id is empty, or the kind's required result id is missing.</exception>
    public void Succeed(decimal costUsd, Guid? resultStoryVersionId, Guid? resultFactSheetId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(costUsd);

        if (resultStoryVersionId == Guid.Empty)
        {
            throw new ArgumentException("The result story version id cannot be empty.", nameof(resultStoryVersionId));
        }

        if (resultFactSheetId == Guid.Empty)
        {
            throw new ArgumentException("The result fact sheet id cannot be empty.", nameof(resultFactSheetId));
        }

        if (Kind == GenerationKind.Story && resultStoryVersionId is null)
        {
            throw new ArgumentException("A story run saves a story version.", nameof(resultStoryVersionId));
        }

        if (Kind == GenerationKind.FactSheet && resultFactSheetId is null)
        {
            throw new ArgumentException("A fact sheet run saves a fact sheet.", nameof(resultFactSheetId));
        }

        if (Status == GenerationRunStatus.Succeeded)
        {
            return;
        }

        RequireStatus(GenerationRunStatus.Running, nameof(Succeed));

        Status = GenerationRunStatus.Succeeded;
        Stage = GenerationStage.Done;
        CostUsd = costUsd;
        ResultStoryVersionId = resultStoryVersionId;
        ResultFactSheetId = resultFactSheetId;
        CountsTowardLimit = Kind != GenerationKind.BibleProposal;
        FinishedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Ends a queued or running run as <c>failed</c> with a D8 code (plan §3.6 sets
    /// <paramref name="countsTowardLimit"/> per outcome). The code is mandatory: <c>ck_generation_runs_failed</c>
    /// rejects a failed row without one.
    /// </summary>
    /// <param name="errorCode">The D8 job-outcome code, never an HTTP code.</param>
    /// <param name="detail">Safe text about the outcome, never content.</param>
    /// <param name="countsTowardLimit">Whether the failure counts toward FR-02-7.</param>
    /// <param name="now">The caller's clock reading, stored as <see cref="FinishedAt"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="errorCode"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The run already ended another way.</exception>
    public void Fail(string errorCode, string? detail, bool countsTowardLimit, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        if (Status == GenerationRunStatus.Failed)
        {
            return;
        }

        RequireOpen(nameof(Fail));

        Status = GenerationRunStatus.Failed;
        ErrorCode = errorCode;
        ErrorDetail = detail;
        CountsTowardLimit = countsTowardLimit;
        PauseReason = null;
        FinishedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Ends a queued or running run as <c>cancelled</c>; the counting flag is left as it was (plan §3.6).</summary>
    /// <param name="now">The caller's clock reading, stored as <see cref="FinishedAt"/>.</param>
    /// <exception cref="InvalidOperationException">The run already ended another way.</exception>
    public void Cancel(DateTimeOffset now)
    {
        if (Status == GenerationRunStatus.Cancelled)
        {
            return;
        }

        RequireOpen(nameof(Cancel));

        Status = GenerationRunStatus.Cancelled;
        PauseReason = null;
        FinishedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Ends a queued or running run as <c>cancelled</c> with a D8 code and an explicit counting flag (02 CR-29,
    /// from T1 QA): the sweeper's cancel of a stuck <c>queued</c> run writes <c>story_generation_cancelled</c> and
    /// <c>counts_toward_limit = false</c>, which <see cref="Cancel(DateTimeOffset)"/> cannot express because it
    /// leaves the flag as it was.
    /// </summary>
    /// <param name="errorCode">The D8 job-outcome code (<c>story_generation_cancelled</c>).</param>
    /// <param name="detail">Safe text about the outcome (<c>stale_run_sweep</c>), never content.</param>
    /// <param name="countsTowardLimit">Whether the cancel counts toward FR-02-7; <see langword="false"/> for a sweep.</param>
    /// <param name="now">The caller's clock reading, stored as <see cref="FinishedAt"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="errorCode"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The run already ended another way.</exception>
    public void Cancel(string errorCode, string? detail, bool countsTowardLimit, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        if (Status == GenerationRunStatus.Cancelled)
        {
            return;
        }

        RequireOpen(nameof(Cancel));

        Status = GenerationRunStatus.Cancelled;
        ErrorCode = errorCode;
        ErrorDetail = detail;
        CountsTowardLimit = countsTowardLimit;
        PauseReason = null;
        FinishedAt = now;
        UpdatedAt = now;
    }

    private void RequireStatus(GenerationRunStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"{operation} needs a {expected} generation run, but run {Id} is {Status}.");
        }
    }

    private void RequireOpen(string operation)
    {
        if (Status is not (GenerationRunStatus.Queued or GenerationRunStatus.Running))
        {
            throw new InvalidOperationException(
                $"{operation} needs a queued or running generation run, but run {Id} is {Status}.");
        }
    }
}
