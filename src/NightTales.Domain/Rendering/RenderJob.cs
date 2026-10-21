using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Domain.Rendering;

/// <summary>
/// One render attempt of an episode (BD5, WF13; 00 §6 <c>render_jobs</c>): the non-content settings of the render
/// (<see cref="Params"/> = <c>render-params.v1</c>, 05 D11), the adapter's progress (<see cref="Stage"/>,
/// <see cref="Percent"/>) and the verdict (<see cref="ErrorCode"/>, <see cref="QualityReport"/>). BD5's partial unique
/// index keeps exactly one <em>active</em> row per episode, and every terminal row stays as that attempt's history
/// (WT49: the newest row is the highest UUIDv7 <see cref="Entity.Id"/>, there is no <c>created_at</c> column).
/// </summary>
/// <remarks>
/// <para>
/// <b>Content-free by construction.</b> <see cref="Params"/> is a <see cref="RenderParamsV1"/> — the D11 counts, ids
/// and presets only. The script body, the scene prompts and the keyword text are read again at submit time from
/// <see cref="StoryVersionId"/> and are never stored on this row (05 P6/P8), so no log, parameter or outbox payload of
/// this aggregate can carry story content.
/// </para>
/// <para>
/// <b>The clock is the caller's.</b> Every method takes the command's or the Worker's reading of the clock; the domain
/// never reads it, so the tests and the outbox payload agree on the instant.
/// </para>
/// <para>
/// <b>A retry is a new row.</b> <see cref="Attempt"/> is written once by <see cref="Create"/>; a re-submitted render
/// inserts another row, so no method ever moves a row backwards to <see cref="RenderJobStatus.Queued"/>.
/// </para>
/// <para>
/// <b>Stamps.</b> <see cref="SubmittedAt"/> is set by <see cref="MarkSubmitted"/> together with
/// <see cref="StartedAt"/> (the node owns the work from that moment) and <see cref="NextPollAt"/>; the poller's
/// progress call keeps advancing <see cref="NextPollAt"/> while the HOLDING poll reports something new, and
/// <see cref="MarkIngesting"/> clears it (WT45) so no tick re-claims the row during the download. A poll that reports
/// nothing new writes nothing, exactly as the task's <c>UpdateProgress</c> contract requires.
/// </para>
/// </remarks>
public sealed partial class RenderJob : AggregateRoot, IUserOwned
{
    /// <summary>The <c>stage</c> text of a row nobody has submitted yet (BD5's acceptance wording: <c>queued</c>).</summary>
    public const string QueuedStage = "queued";

    /// <summary>
    /// BD7's default poll cadence (<c>Render:PollSeconds</c>, 10 s): the distance <see cref="MarkSubmitted"/> and
    /// <see cref="UpdateProgress"/> stamp on <see cref="NextPollAt"/>. <see cref="ReschedulePoll"/> takes the
    /// interval from its caller instead, because the poller passes the adapter's own wait when it gave one.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The catalogued conflict code of "the transition is not allowed in this state" (409, 09 README D16/D17). The
    /// catalogue constant is <c>NightTales.Application.Common.Errors.ErrorCodes.EpisodeInvalidState</c>, which the
    /// Domain may not reference (D1); the unit test <c>RenderJobTests.InvalidStateCode_MatchesTheErrorCatalogue</c>
    /// keeps the two equal. A render job reuses the episode's state-conflict code: the API answers one 409 for a
    /// state that refuses the command, and no separate render-job code is catalogued (09.6.4-T1's
    /// <c>Succeeded_Or_Failed_ReturnsEpisodeInvalidState</c>).
    /// </summary>
    public const string InvalidStateCode = "episode_invalid_state";

    /// <summary>
    /// The catalogued validation code of a refused value (400): an unknown outcome code and a percent that would move
    /// backwards (<c>ErrorCodes.ValidationFailed</c>, spelled here because the Domain may not reference the catalogue).
    /// </summary>
    public const string ValidationFailedCode = "validation_failed";

    /// <summary>The metadata key naming the offending field of a validation error (<c>ErrorMetadata.Field</c>).</summary>
    public const string FieldMetadataKey = "field";

    /// <summary>
    /// The 05 D5 outcome codes <see cref="Fail"/> accepts, in the frozen table's order (05.4.1-T2
    /// <c>RenderOutcomeCodes.All</c>; the test <c>RenderJobTests.OutcomeCodes_MatchTheFrozenContract</c> keeps the two
    /// lists equal). <c>render_cancelled</c> is on the list because D5 catalogues it as an outcome, while the
    /// cancellation path itself ends the row through <see cref="Cancel"/>, never through <see cref="Fail"/> (D5, WT51).
    /// </summary>
    public static readonly IReadOnlyList<string> OutcomeCodes =
    [
        "tts_failed",
        "image_moderated",
        "image_generation_failed",
        "render_assembly_failed",
        "render_node_error",
        "render_lost",
        "render_timeout",
        "render_output_invalid",
        "render_quality_failed",
        "render_cancelled",
    ];

    private RenderJob()
    {
    }

    /// <inheritdoc />
    public Guid UserId { get; private set; }

    /// <summary>The episode this attempt renders; the BD5 partial unique index counts one active row per episode.</summary>
    public Guid EpisodeId { get; private set; }

    /// <summary>
    /// The <em>approved</em> story version the render reads its script and keywords from (never
    /// <c>current_story_version_id</c>, and never a re-read of a possibly-superseded pointer: P6/P8, 02 CR-18).
    /// </summary>
    public Guid StoryVersionId { get; private set; }

    /// <summary>
    /// The credit reservation that pays for this attempt — the <c>ICreditLedger.ReserveAsync</c> result
    /// (<c>uuid NOT NULL</c>, W2 CR-14): the ingest settles exactly this reservation and a failure or cancel refunds
    /// exactly it, by reservation id, never by job id (WT50).
    /// </summary>
    public Guid ReservationId { get; private set; }

    /// <summary>
    /// The worst-case cost in USD this attempt reserved, written once by <see cref="Create"/> from the bound the
    /// command reserved against (03 D8, <c>numeric(12,6)</c>). The open-bounds reader sums it with the generation
    /// runs' bound, which is what makes the platform spend cap cover a render in flight (02.2.2-T2); the value is
    /// never recomputed, so a later price change cannot move an open row.
    /// </summary>
    public decimal WorstCaseUsd { get; private set; }

    /// <summary>Where the attempt is in the BD5 lifecycle (00 §7).</summary>
    public RenderJobStatus Status { get; private set; }

    /// <summary>The adapter's stage text, stored verbatim (05 D4); <see cref="QueuedStage"/> until a node reports.</summary>
    public string Stage { get; private set; } = QueuedStage;

    /// <summary>The adapter's completion percentage, 0–100; it never decreases (BD5).</summary>
    public short Percent { get; private set; }

    /// <summary>The adapter's task id on the node, written by <see cref="MarkSubmitted"/>; never a secret.</summary>
    public string? AdapterTaskId { get; private set; }

    /// <summary>The <c>worker_nodes</c> row the attempt went to, written by <see cref="MarkSubmitted"/>.</summary>
    public Guid? WorkerNodeId { get; private set; }

    /// <summary>The D11 settings document of this attempt; content-free by construction (P6/P8).</summary>
    public RenderParamsV1 Params { get; private set; } = null!;

    /// <summary>The attempt number, counted from 1 by the command; a retry inserts a new row rather than bumping this one.</summary>
    public int Attempt { get; private set; }

    /// <summary>The 05 D5 outcome code of a failed attempt (see <see cref="OutcomeCodes"/>), or <see langword="null"/>.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>
    /// The safe, fixed sentence of the outcome code (05 D15 S4, ≤ 2,000 characters) — never the node's raw error text,
    /// which carries paths and stays on the node (05 C8).
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// The canonical <c>render-quality.v1</c> document of 05.5.1-T2 as JSON text, written by 09.6.3-T2 (with 03.4.2-T1's
    /// <c>images</c> merged in). It is a string on purpose: the typed document
    /// (<c>NightTales.Application.Abstractions.Rendering.RenderQualityReportV1</c>) lives in the Application, which the
    /// Domain may not reference (D1), so this column follows the <c>AutopilotRun.GateResults</c> /
    /// <c>GenerationRun.Models</c> convention of a JSON document held as text.
    /// </summary>
    public string? QualityReport { get; private set; }

    /// <summary>When the node took the work; set by <see cref="MarkSubmitted"/>.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>When the attempt reached a terminal state.</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>When an adapter accepted the task; set by <see cref="MarkSubmitted"/>.</summary>
    public DateTimeOffset? SubmittedAt { get; private set; }

    /// <summary>
    /// The instant the poller may next claim the row (BD5's <c>(status, next_poll_at)</c> index). Stamped by
    /// <see cref="MarkSubmitted"/> (now + <see cref="PollInterval"/>) and advanced by a poll that recorded progress;
    /// <see langword="null"/> once the row is ingesting or terminal, so no tick touches it again (WT45).
    /// </summary>
    public DateTimeOffset? NextPollAt { get; private set; }

    /// <summary>When the cancel was asked for; <see cref="Cancel"/> may follow it, and WT55 makes the unpublished submit skip.</summary>
    public DateTimeOffset? CancelRequestedAt { get; private set; }

    /// <summary>The image count the plan asked for (<c>params.image_plan.needed</c>), copied at creation for the metrics and the DTO.</summary>
    public int? ImagesNeeded { get; private set; }

    /// <summary>
    /// Creates the queued job of an episode (BD5 step 10) and raises <see cref="RenderRequested"/>, which the outbox
    /// interceptor writes into the command's transaction.
    /// </summary>
    /// <remarks>
    /// The id is supplied by the caller because the command must generate it <em>before</em> it reserves the credits:
    /// the reservation's correlation id and the persisted <c>render_job_id</c> are then the same value (WT50, and
    /// 09.6.3-T1's <c>SubmitRenderJob</c> takes that id). The status starts <c>queued</c>, the stage <c>queued</c> and
    /// the percent 0 (BD5's acceptance wording).
    /// </remarks>
    /// <param name="id">The row's UUIDv7 id, generated by the command with <c>Guid.CreateVersion7()</c>.</param>
    /// <param name="userId">The owner.</param>
    /// <param name="episodeId">The episode being rendered.</param>
    /// <param name="storyVersionId">The approved story version the submit reads content from.</param>
    /// <param name="reservationId">The reservation that pays for the attempt (NOT NULL, WT50).</param>
    /// <param name="worstCaseUsd">The non-negative worst-case cost in USD the reservation was taken for (03 D8).</param>
    /// <param name="params">The D11 settings document, built by <c>RenderSubmitMapper.BuildParams</c>.</param>
    /// <param name="attempt">The attempt number, from 1.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>The new aggregate, tracked by the caller's context.</returns>
    public static ErrorOr<RenderJob> Create(
        Guid id,
        Guid userId,
        Guid episodeId,
        Guid storyVersionId,
        Guid reservationId,
        decimal worstCaseUsd,
        RenderParamsV1 @params,
        int attempt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(@params);

        if (id == Guid.Empty)
        {
            return Missing("render_job_id");
        }

        if (userId == Guid.Empty)
        {
            return Missing("user_id");
        }

        if (episodeId == Guid.Empty)
        {
            return Missing("episode_id");
        }

        if (storyVersionId == Guid.Empty)
        {
            return Missing("story_version_id");
        }

        if (reservationId == Guid.Empty)
        {
            return Missing("reservation_id");
        }

        if (worstCaseUsd < 0m)
        {
            return Error.Validation(
                ValidationFailedCode,
                "A render job's worst-case cost cannot be negative.",
                Field("worst_case_usd"));
        }

        if (attempt < 1)
        {
            return Error.Validation(ValidationFailedCode, "A render attempt counts from 1.", Field("attempt"));
        }

        var job = new RenderJob
        {
            Id = id,
            UserId = userId,
            EpisodeId = episodeId,
            StoryVersionId = storyVersionId,
            ReservationId = reservationId,
            WorstCaseUsd = worstCaseUsd,
            Status = RenderJobStatus.Queued,
            Stage = QueuedStage,
            Percent = 0,
            AdapterTaskId = null,
            WorkerNodeId = null,
            Params = @params,
            Attempt = attempt,
            ErrorCode = null,
            ErrorMessage = null,
            QualityReport = null,
            StartedAt = null,
            FinishedAt = null,
            SubmittedAt = null,
            NextPollAt = null,
            CancelRequestedAt = null,
            ImagesNeeded = @params.ImagePlan.Needed,
        };

        job.Raise(new RenderRequested(job.Id, episodeId, userId, now));

        return job;
    }

    /// <summary>
    /// Records that an adapter accepted the task for a node (BD7): <c>queued</c> → <c>submitted</c>, with the task id,
    /// the node, <see cref="SubmittedAt"/>, <see cref="StartedAt"/> and the first poll instant.
    /// </summary>
    /// <param name="adapterTaskId">The adapter's task id (<c>render-submit.v1</c>'s response).</param>
    /// <param name="nodeId">The <c>worker_nodes</c> row the task went to.</param>
    /// <param name="now">The Worker's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of a row that is no longer <c>queued</c> — WT55's re-check after the adapter call.</returns>
    public ErrorOr<Success> MarkSubmitted(string adapterTaskId, Guid nodeId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterTaskId);

        if (nodeId == Guid.Empty)
        {
            return Error.Validation(ValidationFailedCode, "A submitted render names the node it went to.", Field("worker_node_id"));
        }

        if (Status != RenderJobStatus.Queued)
        {
            return InvalidState("be submitted");
        }

        Status = RenderJobStatus.Submitted;
        AdapterTaskId = adapterTaskId;
        WorkerNodeId = nodeId;
        SubmittedAt = now;
        StartedAt = now;
        NextPollAt = now + PollInterval;

        return Result.Success;
    }

    /// <summary>
    /// Records the adapter's progress (BD7): the first report moves <c>submitted</c> → <c>running</c>, every report
    /// stamps <see cref="Stage"/>/<see cref="Percent"/> and the next poll instant.
    /// </summary>
    /// <remarks>
    /// A report whose stage and percent are unchanged writes nothing and answers <c>Success</c>: 09.6.3-T1 relies on
    /// that to raise no realtime event and no write for an idle task. A percent below the current one is refused —
    /// never clamped — because the adapter's monotonic counter is the contract, and a silent clamp would hide a
    /// restarted or wrong task (<c>validation_failed</c>, field <c>percent</c>, 400).
    /// </remarks>
    /// <param name="stage">The adapter's stage text, stored verbatim (05 D4).</param>
    /// <param name="percent">The adapter's completion percentage, 0–100.</param>
    /// <param name="now">The Worker's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of a row that is neither <c>submitted</c> nor <c>running</c>, or the 400 of a percent that decreases or is out of range.</returns>
    public ErrorOr<Success> UpdateProgress(string stage, short percent, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        if (percent is < 0 or > 100)
        {
            return Error.Validation(ValidationFailedCode, "A render percent is between 0 and 100.", Field("percent"));
        }

        if (Status is not (RenderJobStatus.Submitted or RenderJobStatus.Running))
        {
            return InvalidState("report progress");
        }

        if (percent < Percent)
        {
            return Error.Validation(
                ValidationFailedCode,
                "The reported render percent never decreases.",
                Field("percent"));
        }

        // The first report of a submitted row is what makes it running, even when it repeats the queued stage: the
        // adapter answered, which is the state change. Only a repeat on an already-running row is the no-op.
        var first = Status == RenderJobStatus.Submitted;
        Status = RenderJobStatus.Running;

        if (!first && percent == Percent && string.Equals(stage, Stage, StringComparison.Ordinal))
        {
            return Result.Success;
        }

        Stage = stage;
        Percent = percent;
        NextPollAt = now + PollInterval;

        return Result.Success;
    }

    /// <summary>
    /// Moves a job the poller is watching to its next poll instant without recording progress (BD7's
    /// <c>Render:PollSeconds</c> cadence).
    /// </summary>
    /// <remarks>
    /// A poll that reports nothing new — the adapter's <c>deferred</c> answer, or a status that repeats the stored
    /// stage and percent — goes through <see cref="UpdateProgress"/> (or no call at all), so without this method the
    /// row would keep its old <see cref="NextPollAt"/> and be re-claimed by every tick. The claim is
    /// <c>ORDER BY next_poll_at LIMIT PollBatchSize</c>, so those rows would also starve every job behind them.
    /// <para>
    /// The distance is the caller's, not <see cref="PollInterval"/>: BD7's step 3/6 stamps
    /// <c>now + Render:PollSeconds</c>, and the adapter's own <c>Deferred.RetryAfter</c> wins over that cadence when
    /// it gives one, so the poller passes the interval it is about to wait. <see cref="PollInterval"/> stays the
    /// contract default of the two write paths (<see cref="MarkSubmitted"/>, <see cref="UpdateProgress"/>), which do
    /// not know the bound section.
    /// </para>
    /// </remarks>
    /// <param name="now">The Worker's clock reading.</param>
    /// <param name="interval">The distance to the next poll; must be positive.</param>
    /// <returns><c>Success</c>, the 400 of a non-positive interval, or the 409 of a row the poller is not watching.</returns>
    public ErrorOr<Success> ReschedulePoll(DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return Error.Validation(
                ValidationFailedCode,
                "A render poll interval must be positive.",
                Field("interval"));
        }

        if (Status is not (RenderJobStatus.Submitted or RenderJobStatus.Running))
        {
            return InvalidState("be polled again");
        }

        NextPollAt = now + interval;

        return Result.Success;
    }

    /// <summary>
    /// Takes the row out of the poller's claim (UC5, WT45): <c>submitted</c>/<c>running</c> → <c>ingesting</c> and
    /// <see cref="NextPollAt"/> cleared, committed before the first byte of the output is downloaded.
    /// </summary>
    /// <param name="now">The Worker's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of a row the poller is not watching.</returns>
    public ErrorOr<Success> MarkIngesting(DateTimeOffset now)
    {
        if (Status is not (RenderJobStatus.Submitted or RenderJobStatus.Running))
        {
            return InvalidState("start ingesting");
        }

        Status = RenderJobStatus.Ingesting;
        NextPollAt = null;
        StartedAt ??= now;

        return Result.Success;
    }

    /// <summary>Ends the attempt successfully (09.6.3-T2 step 9): <c>ingesting</c> → <c>succeeded</c>.</summary>
    /// <param name="now">The Worker's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of a row that is not ingesting.</returns>
    public ErrorOr<Success> Succeed(DateTimeOffset now)
    {
        if (Status != RenderJobStatus.Ingesting)
        {
            return InvalidState("succeed");
        }

        Status = RenderJobStatus.Succeeded;
        FinishedAt = now;
        NextPollAt = null;

        return Result.Success;
    }

    /// <summary>
    /// Ends the attempt with one of the 05 D5 outcome codes: a <c>queued</c>, submitted, running or ingesting row →
    /// <c>failed</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The code must be on <see cref="OutcomeCodes"/>; anything else is refused as <c>validation_failed</c> (400) rather
    /// than stored, so <c>render_jobs.error_code</c> can never hold an uncatalogued value. <see cref="ErrorMessage"/>
    /// is left empty by this method: the fixed D15 S4 sentence belongs to the node contract, and the node's raw error
    /// text is never stored (05 C8).
    /// </para>
    /// <para>
    /// A <c>queued</c> row fails too (09.6.3-T1, WT55's aftermath): its submit never adopted an
    /// <c>adapter_task_id</c>, either because no node was dispatchable, because the recipe could not be built, or
    /// because the adapter answered a terminal failure to the submit itself. Without this the row would stay
    /// <c>queued</c> forever, holding its <see cref="WorstCaseUsd"/> inside the open-bounds sum and keeping
    /// <c>ux_render_jobs_active_episode</c> busy, while no refund of <see cref="ReservationId"/> could ever run
    /// (<c>SubmitRenderJob</c> and <c>RenderFailureRecorder</c> are the one failure path). Every terminal state is
    /// still refused, so one attempt records exactly one verdict.
    /// </para>
    /// </remarks>
    /// <param name="code">The outcome code.</param>
    /// <param name="now">The Worker's clock reading.</param>
    /// <returns><c>Success</c>, the 400 of an unknown code, or the 409 of a terminal row.</returns>
    public ErrorOr<Success> Fail(string code, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (!OutcomeCodes.Contains(code, StringComparer.Ordinal))
        {
            return Error.Validation(ValidationFailedCode, "The code is not a catalogued render outcome (05 D5).", Field("code"));
        }

        if (Status is not (RenderJobStatus.Queued or RenderJobStatus.Submitted or RenderJobStatus.Running or RenderJobStatus.Ingesting))
        {
            return InvalidState("fail");
        }

        Status = RenderJobStatus.Failed;
        ErrorCode = code;
        FinishedAt = now;
        NextPollAt = null;

        return Result.Success;
    }

    /// <summary>
    /// Raises the <see cref="RenderFailed"/> of a failed attempt (WT28), so the outbox writes its
    /// <c>render_failed.v1</c> row in the same transaction as the refund and the episode transition.
    /// </summary>
    /// <remarks>
    /// Valid only on an already <see cref="RenderJobStatus.Failed"/> row — <see cref="Fail"/> is what decides the
    /// outcome code, and this method never writes state, so it cannot move a row into <c>failed</c> behind the
    /// caller's back. The instant is <see cref="FinishedAt"/>, the reading <see cref="Fail"/> stored, so the event
    /// and the row agree on when the attempt ended (the clock is the caller's; the aggregate never reads one).
    /// <paramref name="final"/> is the retry verdict of the caller (<c>RenderRetryPolicy</c>): <see langword="true"/>
    /// when no retry row follows, <see langword="false"/> while the attempt will be tried again (WT28/WT50).
    /// Raising it twice is the caller's concern: one terminal transition raises exactly one event.
    /// </remarks>
    /// <param name="final">Whether no retry follows this failure.</param>
    /// <returns><c>Success</c>, or the 409 of a row that is not <c>failed</c>.</returns>
    public ErrorOr<Success> RaiseFailed(bool final)
    {
        if (Status != RenderJobStatus.Failed)
        {
            return InvalidState("raise a failure event");
        }

        Raise(new RenderFailed(EpisodeId, Id, ErrorCode!, Attempt, final, FinishedAt!.Value));

        return Result.Success;
    }

    /// <summary>
    /// Raises the <see cref="RenderSucceeded"/> of an ingested attempt (02 CR-23, 09.6.3-T2 step 9), so the outbox
    /// writes its <c>render_succeeded.v1</c> row in the same transaction as the assets, the episode's move to
    /// <c>video_review</c> and the settlement of <see cref="ReservationId"/>.
    /// </summary>
    /// <remarks>
    /// Valid only on an already <see cref="RenderJobStatus.Succeeded"/> row — <see cref="Succeed"/> is what decides
    /// that transition, and this method never writes state, so it cannot move a row behind the caller's back. The
    /// instant is <see cref="FinishedAt"/>, the reading <see cref="Succeed"/> stored, so the event and the row agree
    /// on when the attempt ended (the clock is the caller's). Raising it twice is the caller's concern: one terminal
    /// transition raises exactly one event.
    /// </remarks>
    /// <returns><c>Success</c>, or the 409 of a row that is not <c>succeeded</c>.</returns>
    public ErrorOr<Success> RaiseSucceeded()
    {
        if (Status != RenderJobStatus.Succeeded)
        {
            return InvalidState("raise a success event");
        }

        Raise(new RenderSucceeded(EpisodeId, Id, FinishedAt!.Value));

        return Result.Success;
    }

    /// <summary>
    /// Stores the canonical verdict of the attempt (05 D10) — <c>render-quality.v1</c> plus 03.4.2-T1's
    /// <c>images</c> block, as JSON text, written by the ingest (09.6.3-T2 step 8) inside the transaction that
    /// decides the attempt's fate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The report is written on both verdicts: a failed one keeps the checks that failed, which is what the owner
    /// and 10.6.2-T1 read after a <c>render_quality_failed</c> attempt. Only an in-flight row accepts it: a terminal
    /// row's verdict is history and never moves, so a late duplicate of the ingest cannot rewrite it.
    /// </para>
    /// <para>
    /// The text is stored verbatim — the Domain cannot read the typed document (D1) — so the caller is the one that
    /// serialises the Application layer's <c>RenderQualityReportV1</c> with the strict node JSON options. Blank text
    /// is refused rather than stored: an empty <c>quality_report</c> would read as "no report" (05 P2).
    /// </para>
    /// </remarks>
    /// <param name="reportJson">The serialised <c>render-quality.v1</c> document.</param>
    /// <returns><c>Success</c>, the 400 of a blank document, or the 409 of a terminal row.</returns>
    public ErrorOr<Success> RecordQualityReport(string reportJson)
    {
        ArgumentNullException.ThrowIfNull(reportJson);

        if (string.IsNullOrWhiteSpace(reportJson))
        {
            return Error.Validation(
                ValidationFailedCode,
                "A quality report carries the verdict document (05 D10).",
                Field("quality_report"));
        }

        if (Status is not (RenderJobStatus.Running or RenderJobStatus.Ingesting))
        {
            return InvalidState("record a quality report");
        }

        QualityReport = reportJson;

        return Result.Success;
    }

    /// <summary>
    /// Asks for the cancellation of an in-flight attempt (BD8): <c>submitted</c> or <c>running</c> stamps
    /// <see cref="CancelRequestedAt"/> so the adapter can be told to cancel and WT55's re-check after the adapter call
    /// skips the submit.
    /// </summary>
    /// <remarks>
    /// Idempotent: a row that already carries <see cref="CancelRequestedAt"/> answers <c>Success</c> without a second
    /// stamp, so the request path of 09.6.4-T1 writes no second outbox row and no second refund (WT50/WT51). The
    /// refund itself belongs to the terminal <see cref="Cancel"/>, never to this request.
    /// </remarks>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of a queued, ingesting or terminal row.</returns>
    public ErrorOr<Success> RequestCancel(DateTimeOffset now)
    {
        if (CancelRequestedAt is not null)
        {
            return Result.Success;
        }

        if (Status is not (RenderJobStatus.Submitted or RenderJobStatus.Running))
        {
            return InvalidState("be cancelled while it is in flight");
        }

        CancelRequestedAt = now;
        Raise(new RenderCancelRequested(Id, EpisodeId, UserId, now));

        return Result.Success;
    }

    /// <summary>
    /// Ends the attempt as <c>cancelled</c> (BD8, WT51): a <c>queued</c> row cancels at once, and a
    /// <c>submitted</c>/<c>running</c> row cancels when the adapter reports the terminal <c>cancelled</c> (the request
    /// path sets <see cref="CancelRequestedAt"/> first). An <c>ingesting</c> row is refused — the output is already
    /// being stored — and every terminal state is refused as well.
    /// </summary>
    /// <param name="now">The command's or the Worker's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of an ingesting or terminal row.</returns>
    public ErrorOr<Success> Cancel(DateTimeOffset now)
    {
        if (Status is RenderJobStatus.Ingesting or RenderJobStatus.Succeeded
            or RenderJobStatus.Failed or RenderJobStatus.Cancelled)
        {
            return InvalidState("be cancelled");
        }

        Status = RenderJobStatus.Cancelled;
        FinishedAt = now;
        NextPollAt = null;

        return Result.Success;
    }

    private static Error Missing(string field) =>
        Error.Validation(ValidationFailedCode, "A render job needs every id it is created with.", Field(field));

    private static Error InvalidState(string transition) =>
        Error.Conflict(InvalidStateCode, $"The render job cannot {transition} in its current state.");

    private static Dictionary<string, object> Field(string field) =>
        new(StringComparer.Ordinal) { [FieldMetadataKey] = field };
}
