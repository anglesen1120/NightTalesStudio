using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The reasons an autopilot gate stops and the owner has to act (02.8.2 plan §4 UC6). The codes are the wire
/// values of the outbox payload's <c>reason</c>, of the <c>autopilot.owner_action_required</c> audit row and of
/// the run log's copy key, so they are spelled once, here.
/// </summary>
public static class OwnerActionReasons
{
    /// <summary>Check 15 of plan §3.1: a true-crime episode is held for the owner by design, never a failure.</summary>
    public const string TrueCrimeHold = "true_crime_hold";

    /// <summary>A check did not pass, or the gate could not evaluate one (fail-closed).</summary>
    public const string FailedChecks = "failed_checks";

    /// <summary>The gate ran in dry-run mode (02.8.2-T3): the verdict is recorded and nothing is approved.</summary>
    public const string DryRun = "dry_run";

    /// <summary>The gate passed but the mode requires the owner's own approval before anything is approved.</summary>
    public const string OwnerApprovalRequired = "owner_approval_required";

    /// <summary>The draft-time moderation block is missing and the moderator could not be reached (fail closed).</summary>
    public const string ModeratorUnavailable = "moderator_unavailable";

    /// <summary>Every code this feature may put in a notice, in copy-key order.</summary>
    public static readonly IReadOnlyList<string> All =
        [TrueCrimeHold, FailedChecks, DryRun, OwnerApprovalRequired, ModeratorUnavailable];

    /// <summary>Whether <paramref name="reason"/> is one of the five codes above.</summary>
    /// <param name="reason">The code to check.</param>
    public static bool IsKnown(string reason) => All.Contains(reason, StringComparer.Ordinal);

    /// <summary>
    /// The copy key the run log and the alert use for <paramref name="reason"/> (02.8.2 plan §6, 10.4.3-T1's
    /// copy table). The stop itself is always the waiting-owner state; only the true-crime hold, the dry run and
    /// the moderator outage have a more specific line.
    /// </summary>
    /// <param name="reason">One of the codes above.</param>
    public static string CopyKeyOf(string reason) => reason switch
    {
        TrueCrimeHold => "autopilot.gate.hold.true_crime",
        DryRun => "autopilot.run.gate.dry_run_badge",
        ModeratorUnavailable => "autopilot.gate.failed.moderation_gate",
        _ => "autopilot.run.state.waiting_owner",
    };
}

/// <summary>
/// Tells the owner that one autopilot run stopped at a gate (02.8.2 plan §4 UC6): it adds the
/// <c>AutopilotOwnerActionRequired</c> event — which the outbox interceptor turns into the
/// <c>autopilot_owner_action_required.v1</c> row — the <c>autopilot.owner_action_required</c> audit row and the
/// <c>autopilot_owner_action_required</c> <c>email_messages</c> row to the caller's unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never saves and never commits.</b> The gate evaluator that calls it — in the same transaction as the
/// <c>gate_results</c> write — owns the one <c>SaveChangesAsync</c> of the unit, so the verdict, its audit row and
/// the notice the Worker will deliver commit or roll back together (00 §5.2, 09 README D5). A caller outside the
/// Mediator pipeline opens the transaction itself, which is also what <c>IAuditWriter</c> requires.
/// </para>
/// <para>
/// <b>The e-mail is queued here, not by the outbox delivery.</b> That is the whole point of the move: an outbox
/// handler runs in a transaction of its own after the verdict has already committed, and the dispatcher marks its
/// row processed <em>after</em> it returns, so a crash in that window would redeliver the event and the owner would
/// get the notice twice (<c>EmailMessage</c> carries no idempotency key). Queueing in the verdict's own transaction
/// — the precedent of <c>ConnectChannelConnectionHandler</c> (06.1.2-T2) — makes the message exactly-once with the
/// verdict it belongs to, and leaves the handler with the idempotent <c>EpisodeUpdated</c> publish only (09 README
/// D11, 02.8.2 plan §5).
/// </para>
/// <para>
/// <b>It changes nothing of the episode.</b> A stop leaves the episode in <c>story_review</c>/<c>video_review</c>
/// and the owner decides; this service only records that the decision is now the owner's.
/// </para>
/// <para>
/// <b>Fail closed, never throws.</b> A missing run, a run without an episode, a missing episode/series/owner row,
/// an unknown reason code and a refused e-mail queue are answered with an error instead of an exception, and
/// nothing is written in those cases: a notification that cannot be addressed must not be able to approve or
/// reject anything by mistake, and a half-written notice is no notice at all.
/// </para>
/// </remarks>
/// <param name="db">The write model of the caller's unit of work; a tracked run is what raises the event.</param>
/// <param name="audit">The append-only audit writer of the caller's transaction.</param>
/// <param name="email">Queues the <c>autopilot_owner_action_required</c> message; never sends it (09.2.6-T3).</param>
/// <param name="profiles">The owner's UI locale, which is the render locale of the message (09 README D16).</param>
/// <param name="clock">The application clock; the row's instant and the event's <c>occurredAt</c>.</param>
/// <param name="logger">The ids-only log of this service.</param>
public sealed partial class AutopilotNotificationService(
    IAppDbContext db,
    IAuditWriter audit,
    IEmailSender email,
    IUserProfileReader profiles,
    IClock clock,
    ILogger<AutopilotNotificationService> logger)
{
    /// <summary>The frozen wire <c>messageKey</c> of the audit row (02.8.2 plan §4, README D13).</summary>
    public const string AuditAction = "autopilot.owner_action_required";

    /// <summary>The audit row's <c>entity_type</c>; the affected row is the episode, whose state does not move.</summary>
    public const string EntityType = "episode";

    /// <summary>The catalog name of the owner-action e-mail (02.8.2 plan §5, 09 README BD9).</summary>
    public const string EmailTemplate = "autopilot_owner_action_required";

    /// <summary>
    /// The static episode path the e-mail points at. It is a plain path, not a minted link: this template carries
    /// no token and the owner is already signed in when they open it.
    /// </summary>
    /// <param name="episodeId">The episode the notice is about.</param>
    public static string DeepLinkOf(Guid episodeId) => $"/episodes/{episodeId}";

    /// <summary>
    /// The <c>failedChecks</c> value of the template: the failing check ids in words, and — for a true-crime hold
    /// — the sentence plan §5 requires, in the recipient's locale. Ids and codes only, never story text.
    /// </summary>
    /// <param name="reason">The notice's reason code (<see cref="OwnerActionReasons"/>).</param>
    /// <param name="failures">The failing check ids; empty for a stop that failed no check (dry run).</param>
    /// <param name="locale">The recipient's locale, which picks the sentence's language.</param>
    public static string FailedChecksText(string reason, IReadOnlyList<string> failures, Language locale)
    {
        ArgumentNullException.ThrowIfNull(failures);

        var words = new List<string>(failures.Count);
        foreach (var failure in failures)
        {
            words.Add(InWords(failure));
        }

        if (words.Count == 0)
        {
            // A dry run and a held run fail no check: naming the reason is the only honest thing to show there.
            words.Add(InWords(reason));
        }

        var text = string.Join(", ", words);

        return reason == OwnerActionReasons.TrueCrimeHold
            ? $"{text} — {HoldSentence(locale)}"
            : text;
    }

    /// <summary>
    /// Adds the owner-action notice — the outbox event, its audit row and the <c>email_messages</c> row — to the
    /// caller's unit of work.
    /// </summary>
    /// <param name="runId">The run whose gate stopped.</param>
    /// <param name="reason">One of <see cref="OwnerActionReasons"/>.</param>
    /// <param name="failures">The failing gate check ids; an empty list for a stop that has none (dry run).</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// <c>Success</c> once the three rows are in the unit of work; an <c>Error.Failure</c> with
    /// <c>internal_error</c> when the run is missing, has no episode, names an episode/series/owner row that is
    /// gone, or <paramref name="reason"/> is not a known code — and, unchanged, the queueing error when the e-mail
    /// queue refuses the message. Nothing is added to the unit in any of those cases.
    /// </returns>
    public async ValueTask<ErrorOr<Success>> NotifyOwnerActionRequiredAsync(
        Guid runId,
        string reason,
        IReadOnlyList<string> failures,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(failures);

        if (!OwnerActionReasons.IsKnown(reason))
        {
            LogUnknownReason(runId, reason);

            return Error.Failure(ErrorCodes.InternalError, $"'{reason}' is not an owner-action reason code.");
        }

        var run = await db.AutopilotRuns
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, ct)
            .ConfigureAwait(false);

        if (run is null)
        {
            LogRunMissing(runId);

            return Error.Failure(ErrorCodes.InternalError, $"Autopilot run {runId} does not exist.");
        }

        if (run.EpisodeId is not { } episodeId)
        {
            LogEpisodeMissing(runId);

            return Error.Failure(ErrorCodes.InternalError, $"Autopilot run {runId} has no episode to notify about.");
        }

        // The message names the episode, its series and its owner, so all three rows must be readable before
        // anything is written: a missing one makes the notice unaddressable, and fail-closed means none of it
        // reaches the unit of work (the reads themselves write nothing).
        var episode = await db.Episodes
            .FirstOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);
        var series = await db.Series
            .FirstOrDefaultAsync(candidate => candidate.Id == run.SeriesId, ct)
            .ConfigureAwait(false);
        var profile = await profiles.GetAsync(run.UserId, ct).ConfigureAwait(false);

        if (episode is null || series is null || profile is null)
        {
            LogRowsMissing(runId, episodeId);

            return Error.Failure(
                ErrorCodes.InternalError,
                $"The owner-action notice of autopilot run {runId} cannot be addressed: a row it names is gone.");
        }

        // Queued first, in this transaction: a refusal is an error answer the caller rolls back and nothing else of
        // the notice was added yet (the ConnectChannelConnectionHandler precedent, 06.1.2-T2). Queueing here, rather
        // than in the outbox delivery, is what keeps an at-least-once redelivery from queueing a second message
        // (09 README D11, 02.8.2 plan §5).
        var queued = await email
            .QueueAsync(
                new EmailMessage(
                    run.UserId,
                    EmailTemplate,
                    profile.UiLocale,
                    EmailParameters(
                        series.Title,
                        episode.Title,
                        run.StartedAt,
                        reason,
                        failures,
                        episodeId,
                        profile.UiLocale)),
                ct)
            .ConfigureAwait(false);
        if (queued.IsError)
        {
            LogEmailQueueFailed(runId, episodeId, queued.FirstError.Code);

            return queued.Errors;
        }

        var codes = failures.ToArray();
        var now = clock.UtcNow;

        run.RaiseOwnerActionRequired(reason, codes, OwnerActionReasons.CopyKeyOf(reason), now);

        // Ids, the reason and the failing check codes only (D19, 09 README BD16): never a story text, a brief, a
        // fact sheet or a source URL. The key names are the ones of plan §4's audit table.
        var data = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runId"] = runId,
            ["episodeId"] = episodeId,
            ["reason"] = reason,
            ["failures"] = codes,
        });

        await audit
            .WriteAsync(
                new AuditEntry(
                    ActorUserId: run.UserId,
                    // The row says the platform stopped the run on the owner's behalf; the owner is the subject
                    // (the shape an autopilot write has wherever it is not a command's own audit row).
                    ActorType: ActorType.System,
                    SubjectUserId: run.UserId,
                    Action: AuditAction,
                    Category: Category.Episodes,
                    Visibility: Visibility.User,
                    EntityType: EntityType,
                    EntityId: episodeId,
                    DataJson: data,
                    Ip: null,
                    UserAgent: null,
                    ActorLabel: null,
                    OccurredAt: now),
                ct)
            .ConfigureAwait(false);

        LogNotified(runId, episodeId, reason, codes.Length, queued.Value);

        return Result.Success;
    }

    /// <summary>
    /// The five substitution values of the <c>autopilot_owner_action_required</c> template — exactly the keys its
    /// catalog entry declares: the two titles, the run's slot, the failing checks in words and the episode path.
    /// The titles are the owner's own metadata, never story text, and no brief, fact sheet or source URL reaches
    /// the message.
    /// </summary>
    /// <param name="seriesTitle">The run's series, for its title.</param>
    /// <param name="episodeTitle">The run's episode, for its (optional) title.</param>
    /// <param name="slot">The run's start instant, which is the only slot the row carries today.</param>
    /// <param name="reason">The notice's reason code (<see cref="OwnerActionReasons"/>).</param>
    /// <param name="failures">The failing check ids; empty for a stop that failed no check (dry run).</param>
    /// <param name="episodeId">The episode the notice is about, for its path.</param>
    /// <param name="locale">The recipient's locale, which picks the hold sentence's language.</param>
    private static Dictionary<string, string> EmailParameters(
        string seriesTitle,
        string? episodeTitle,
        DateTimeOffset slot,
        string reason,
        IReadOnlyList<string> failures,
        Guid episodeId,
        Language locale) =>
        new(StringComparer.Ordinal)
        {
            ["seriesTitle"] = seriesTitle,
            ["episodeTitle"] = episodeTitle ?? string.Empty,
            // `slot_at` arrives with 02.8.3-T1's state machine; the run's own start is the only slot the row has
            // today, and it is rendered round-trip (UTC) so the owner can place it in their own clock.
            ["slot"] = slot.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["failedChecks"] = FailedChecksText(reason, failures, locale),
            ["deepLink"] = DeepLinkOf(episodeId),
        };

    /// <summary><c>self_review_mean_score</c> reads as "self review mean score"; the code itself never changes.</summary>
    /// <param name="code">A gate check id or a reason code.</param>
    private static string InWords(string code) => code.Replace('_', ' ').Trim();

    /// <summary>The true-crime sentence of plan §5, in the recipient's language.</summary>
    /// <param name="locale">The recipient's UI locale.</param>
    private static string HoldSentence(Language locale) => locale == Language.Vi
        ? "bảng dữ kiện và truyện đang chờ bạn phê duyệt"
        : "the fact sheet and story wait for your approval";

    [LoggerMessage(
        EventId = 6430,
        Level = LogLevel.Information,
        Message = "Autopilot run {RunId} stopped at the gate for episode {EpisodeId} ({Reason}, {FailureCount} failing checks); the owner-action e-mail {EmailMessageId} was queued in the verdict's transaction.")]
    private partial void LogNotified(Guid runId, Guid episodeId, string reason, int failureCount, Guid emailMessageId);

    [LoggerMessage(
        EventId = 6431,
        Level = LogLevel.Warning,
        Message = "The owner-action notice of autopilot run {RunId} carries an unknown reason code {Reason}; nothing was written.")]
    private partial void LogUnknownReason(Guid runId, string reason);

    [LoggerMessage(
        EventId = 6432,
        Level = LogLevel.Warning,
        Message = "The owner-action notice of autopilot run {RunId} was not written: the run does not exist in this scope.")]
    private partial void LogRunMissing(Guid runId);

    [LoggerMessage(
        EventId = 6433,
        Level = LogLevel.Warning,
        Message = "The owner-action notice of autopilot run {RunId} was not written: the run has no episode.")]
    private partial void LogEpisodeMissing(Guid runId);

    [LoggerMessage(
        EventId = 6500,
        Level = LogLevel.Warning,
        Message = "The owner-action notice of autopilot run {RunId} for episode {EpisodeId} was not written: the e-mail queue refused it ({ErrorCode}).")]
    private partial void LogEmailQueueFailed(Guid runId, Guid episodeId, string errorCode);

    [LoggerMessage(
        EventId = 6501,
        Level = LogLevel.Warning,
        Message = "The owner-action notice of autopilot run {RunId} for episode {EpisodeId} was not written: the episode, its series or its owner is gone.")]
    private partial void LogRowsMissing(Guid runId, Guid episodeId);
}
