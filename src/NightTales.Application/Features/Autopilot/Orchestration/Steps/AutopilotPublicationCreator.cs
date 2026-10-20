using System.Text;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The one place an autopilot run turns a finished, approved video into a publication (02.8.3 plan §3.4, §3.5 rows
/// <c>video_gate → publication_created</c> and <c>waiting_owner → publication_created</c>, §7.4): it applies the
/// missed-slot rule, resolves the run's channel, sends the same <c>CreatePublicationCommand</c> the owner's route
/// sends, and attaches the row it created.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two callers, one rule.</b> <see cref="VideoGateStep"/> calls this after a live pass approved the cut, and
/// <c>WaitingOwnerStep</c> calls it when the owner approved it by hand. Both must obey the same timing guard —
/// <see cref="AutopilotTiming.EvaluateVetoWindow"/> decides wait / create / slot missed — which is why the decision
/// lives here instead of in either step.
/// </para>
/// <para>
/// <b>Timing (plan §3.4).</b> The publication is created only while
/// <c>publication_create_at ≤ now &lt; upload_at − MinVetoWindowHours</c>; before that the step is
/// <see cref="StepResultKind.Idle"/> (the tick re-enters it every 15 minutes), at or after the cutoff the run ends
/// <c>failed</c> with <c>pause_reason = slot_missed</c>. The command recomputes the veto window from the same
/// settings and refuses a window shorter than the minimum, so a late create gets the shortened — never sub-minimum
/// — window the plan asks for without this class holding a second copy of the formula.
/// </para>
/// <para>
/// <b>Channel (plan §3.6 rows 6 and 6b, §3.7).</b> The connection comes from the run's own authorization snapshot:
/// no authorization, no channel recorded on it (S66-7 leaves the column nullable), or a row this scope cannot read
/// ends the run <c>autopilot_channel_missing</c>; an inactive connection and an unsupported platform are §3.6
/// triggers, so they <em>pause</em> the run instead of failing it.
/// </para>
/// <para>
/// <b>Privacy (plan §3.1, §7.4).</b> The submitted value is the series' <c>privacy</c>, except while the channel is
/// pre-audit, where it is <c>private</c> in <em>both</em> pre-audit modes (§7.4 keeps the cadence private and pauses
/// on <c>pre_audit_pause_after</c>; the pause itself is 02.8.3-T4's trigger 14, so no run reaches this step after
/// it). 06.2.1's handler applies the same project-wide override on top (06 README D12), so while the project is
/// unaudited the stored value is <c>private</c> whatever is submitted here; the choice matters only after the audit
/// is approved.
/// </para>
/// <para>
/// <b>Idempotency.</b> The command is not an <c>IIdempotentCommand</c>; the local guard is the run's
/// <c>step</c> plus <see cref="AutopilotRun.PublicationId"/>, and the database's own guard is the partial unique
/// <c>ux_publications_active_episode_platform</c>: a repeated create for an episode that already has a live
/// publication answers <c>publication_invalid_state</c>, which is adopted rather than retried. Nothing here logs a
/// title, a description, a tag or a channel name (D19).
/// </para>
/// </remarks>
public sealed partial class AutopilotPublicationCreator
{
    private readonly ISender _sender;
    private readonly IAutopilotStepReader _reader;
    private readonly IChannelAuditPolicy _auditPolicy;
    private readonly ILogger<AutopilotPublicationCreator> _logger;

    /// <summary>Builds the creator over the run scope's context.</summary>
    /// <param name="sender">The mediator the publication command is sent through.</param>
    /// <param name="db">The per-run scope's context, already pinned to the run's owner.</param>
    /// <param name="youTubeOptions">The bound <c>YouTube</c> section: the audit status pre-audit mode reads.</param>
    /// <param name="logger">The ids-only log of this creator.</param>
    /// <remarks>
    /// Marked for the container on purpose: the two explicit-seam ctors below are equally satisfiable the day
    /// <see cref="IChannelAuditPolicy"/> is registered, and without this attribute that registration would turn
    /// into an ambiguous-constructor boot failure.
    /// </remarks>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AutopilotPublicationCreator(
        ISender sender,
        IAppDbContext db,
        YouTubeOptions youTubeOptions,
        ILogger<AutopilotPublicationCreator> logger)
        : this(sender, new EfAutopilotStepReader(db), new PlatformChannelAuditPolicy(youTubeOptions), logger)
    {
    }

    /// <summary>Builds the creator over explicit seams, for tests.</summary>
    /// <param name="sender">The mediator the publication command is sent through.</param>
    /// <param name="reader">The episode, channel and publication reads.</param>
    /// <param name="auditPolicy">The pre-audit answer of the run's channel.</param>
    /// <param name="logger">The ids-only log of this creator.</param>
    public AutopilotPublicationCreator(
        ISender sender,
        IAutopilotStepReader reader,
        IChannelAuditPolicy auditPolicy,
        ILogger<AutopilotPublicationCreator> logger)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(auditPolicy);
        ArgumentNullException.ThrowIfNull(logger);

        _sender = sender;
        _reader = reader;
        _auditPolicy = auditPolicy;
        _logger = logger;
    }

    /// <summary>Builds the creator over explicit seams without a log, for tests and for the waiting-owner step.</summary>
    /// <param name="sender">The mediator the publication command is sent through.</param>
    /// <param name="reader">The episode, channel and publication reads.</param>
    /// <param name="auditPolicy">The pre-audit answer of the run's channel.</param>
    public AutopilotPublicationCreator(
        ISender sender,
        IAutopilotStepReader reader,
        IChannelAuditPolicy auditPolicy)
        : this(sender, reader, auditPolicy, Microsoft.Extensions.Logging.Abstractions.NullLogger<AutopilotPublicationCreator>.Instance)
    {
    }

    /// <summary>
    /// Creates the run's publication if the slot's timing allows it, or reports why it did not.
    /// </summary>
    /// <param name="context">The run, its series, settings and timing, and the tick's clock reading.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// <see cref="StepResultKind.Advanced"/> with <c>publication</c> when a publication was created (the run is then
    /// in <c>publication_created</c>), <see cref="StepResultKind.Idle"/> while the create window has not opened,
    /// <see cref="StepResultKind.Stopped"/> when the slot is missed, the channel is unusable or the create was
    /// refused for good, and <see cref="StepResultKind.Retry"/> for an error a later tick may outlive.
    /// </returns>
    public async ValueTask<StepResult> TryCreateAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;
        var now = context.Now;

        // Local guard: the run created its publication already (a redelivered event re-enters the step).
        if (run.PublicationId is not null)
        {
            return StepResult.Idle;
        }

        if (run.EpisodeId is not { } episodeId)
        {
            LogChannelUnusable(run.Id, AutopilotStepCodes.EpisodeMissing);

            return StepResult.Retry(AutopilotStepCodes.EpisodeMissing);
        }

        var verdict = AutopilotTiming.EvaluateVetoWindow(now, context.Timing, context.TimingOptions, context.Settings);

        switch (verdict.Decision)
        {
            case VetoWindowDecision.WaitUntil:
                return StepResult.Idle;

            case VetoWindowDecision.SlotMissed:
                run.Fail(AutopilotRun.SlotMissedReason, now);
                LogSlotMissed(run.Id);

                return StepResult.Stopped(AutopilotStepCodes.SlotMissed);
        }

        var channel = await _reader.FindChannelAsync(run.AuthorizationId, ct).ConfigureAwait(false);
        var channelResult = CheckChannel(run, channel, now);
        if (channelResult is not null)
        {
            return channelResult;
        }

        var episode = await _reader.FindEpisodeAsync(episodeId, ct).ConfigureAwait(false);
        if (episode is null)
        {
            run.Fail(AutopilotStepCodes.EpisodeMissing, now);
            LogCreateRefused(run.Id, AutopilotStepCodes.EpisodeMissing);

            return StepResult.Stopped(AutopilotStepCodes.EpisodeMissing);
        }

        var privacy = await ResolvePrivacyAsync(context, channel!.Id, ct).ConfigureAwait(false);
        var title = TitleOf(episode, context.Series);
        var description = DescriptionOf(episode, title);

        var created = await _sender
            .Send(
                new CreatePublicationCommand(
                    episode.Id,
                    channel.Id,
                    title,
                    description,
                    [],
                    PrivacyWire(privacy),
                    run.SlotAt,
                    run.AuthorizationId),
                ct)
            .ConfigureAwait(false);

        if (created.IsError)
        {
            return await MapRefusalAsync(run, episodeId, created.FirstError, now, ct).ConfigureAwait(false);
        }

        run.AttachPublication(created.Value.Id);
        run.AdvanceTo(AutopilotRunState.PublicationCreated, AutopilotRunStep.Publication, now);
        LogCreated(run.Id, created.Value.Id, run.SlotAt);

        return StepResult.Advanced(AutopilotRunStep.Publication);
    }

    /// <summary>
    /// Applies plan §3.6 rows 6 and 6b to the channel the run's authorization names: a missing channel ends the run,
    /// an unusable one pauses it.
    /// </summary>
    /// <returns><see langword="null"/> when the channel may be published through.</returns>
    private StepResult? CheckChannel(
        AutopilotRun run,
        AutopilotChannelSnapshot? channel,
        DateTimeOffset now)
    {
        if (channel is null)
        {
            // No authorization, one that was revoked, one that records no channel (S66-7), or a row outside this
            // owner's scope: none of them can name a channel to publish through.
            run.Fail(AutopilotStepCodes.ChannelMissing, now);
            LogChannelUnusable(run.Id, AutopilotStepCodes.ChannelMissing);

            return StepResult.Stopped(AutopilotStepCodes.ChannelMissing);
        }

        if (channel.Platform != NightTales.Domain.Publishing.Platform.YouTube)
        {
            run.Pause(AutopilotPauseReason.ChannelUnsupported, now);
            LogChannelUnusable(run.Id, AutopilotStepCodes.ChannelUnsupported);

            return StepResult.Stopped(AutopilotStepCodes.ChannelUnsupported);
        }

        if (channel.Status != ChannelConnectionStatus.Active)
        {
            run.Pause(AutopilotPauseReason.ChannelNotActive, now);
            LogChannelUnusable(run.Id, AutopilotStepCodes.ChannelNotActive);

            return StepResult.Stopped(AutopilotStepCodes.ChannelNotActive);
        }

        return null;
    }

    /// <summary>The privacy the create submits: the series' value, held private while the channel is pre-audit.</summary>
    private async ValueTask<PrivacyStatus> ResolvePrivacyAsync(
        AutopilotStepContext context,
        Guid channelConnectionId,
        CancellationToken ct)
    {
        var preAudit = await _auditPolicy.IsPreAuditAsync(channelConnectionId, ct).ConfigureAwait(false);
        if (!preAudit)
        {
            return context.Settings.Privacy;
        }

        // Plan §7.4: while the project is unaudited the create is private in both modes. `pause_after_n_private`
        // differs only by T4's trigger 14, which pauses the series once `pre_audit_pause_after` videos are locked,
        // so this step never sees the run that would follow them.
        return PrivacyStatus.Private;
    }

    /// <summary>Maps a refused create: adopt a live publication, pause, end or retry (plan §7.3).</summary>
    private async ValueTask<StepResult> MapRefusalAsync(
        AutopilotRun run,
        Guid episodeId,
        Error error,
        DateTimeOffset now,
        CancellationToken ct)
    {
        LogCreateRefused(run.Id, error.Code);

        switch (error.Code)
        {
            case ErrorCodes.ChannelNotActive:
                run.Pause(AutopilotPauseReason.ChannelNotActive, now);

                return StepResult.Stopped(AutopilotStepCodes.ChannelNotActive);

            // Exactly the live-publication collision of the unique index `ux_publications_active_episode_platform`
            // (06.2.1 answers it `publication_invalid_state`); `publication_conflict` is a different failure and
            // ends the run with a code rather than adopting a row this run did not create.
            case ErrorCodes.PublicationInvalidState:
                {
                    // The episode already has a live publication: this run's own lost save, or a create from another
                    // path. Adopting it keeps "one publication per episode and platform" true without a second row.
                    // Only the row this run created may be adopted: another authorization's (or a manual) publication
                    // for the same episode means the slot's create can never succeed, which is a failure, not a pass.
                    var existing = await _reader.FindActivePublicationAsync(episodeId, ct).ConfigureAwait(false);
                    if (existing is not null && existing.AutopilotAuthorizationId == run.AuthorizationId)
                    {
                        run.AttachPublication(existing.Id);
                        run.AdvanceTo(AutopilotRunState.PublicationCreated, AutopilotRunStep.Publication, now);
                        LogAdopted(run.Id, existing.Id);

                        return StepResult.Advanced(AutopilotRunStep.Publication);
                    }

                    run.Fail(AutopilotStepCodes.PublicationCreateFailed, now);

                    return StepResult.Stopped(AutopilotStepCodes.PublicationCreateFailed);
                }

            // A transient refusal: the same tick's retry, or the next one, may succeed and the slot is still open.
            case ErrorCodes.ServiceUnavailable:
                return StepResult.Retry(error.Code);

            default:
                run.Fail(AutopilotStepCodes.PublicationCreateFailed, now);

                return StepResult.Stopped(AutopilotStepCodes.PublicationCreateFailed);
        }
    }

    /// <summary>
    /// The publication title: the episode's own title when the idea plan gave one, else the series title, cut at a
    /// rune boundary to YouTube's limit. Nothing exists in 02 today that writes a publication-specific title, so the
    /// episode/series default is what a manual create would submit.
    /// </summary>
    private static string TitleOf(AutopilotRenderEpisode episode, SeriesAggregate series)
    {
        var title = (episode.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            title = series.Title.Trim();
        }

        return Shorten(title, YouTubeMetadataRules.TitleMaxChars);
    }

    /// <summary>The publication description: the episode's brief, which is 1–1,000 characters by construction.</summary>
    private static string DescriptionOf(AutopilotRenderEpisode episode, string title)
    {
        var description = episode.Brief.Trim();

        return description.Length > 0 ? description : title;
    }

    /// <summary>Cuts <paramref name="text"/> after <paramref name="maxRunes"/> code points, never mid-rune.</summary>
    private static string Shorten(string text, int maxRunes)
    {
        if (text.Length <= maxRunes)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var taken = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (taken++ == maxRunes)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>The stored and submitted spelling of <see cref="PrivacyStatus"/> (the publication route's values).</summary>
    private static string PrivacyWire(PrivacyStatus privacy) => privacy switch
    {
        PrivacyStatus.Public => "public",
        PrivacyStatus.Unlisted => "unlisted",
        _ => "private",
    };

    [LoggerMessage(
        EventId = 6569,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} missed its slot before its publication was created; the run ends slot_missed.")]
    private partial void LogSlotMissed(Guid runId);

    [LoggerMessage(
        EventId = 6570,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} cannot publish through its channel ({ReasonCode}).")]
    private partial void LogChannelUnusable(Guid runId, string reasonCode);

    [LoggerMessage(
        EventId = 6571,
        Level = LogLevel.Information,
        Message = "Autopilot run {RunId} created publication {PublicationId} for slot {SlotAt}.")]
    private partial void LogCreated(Guid runId, Guid publicationId, DateTimeOffset slotAt);

    [LoggerMessage(
        EventId = 6572,
        Level = LogLevel.Warning,
        Message = "The publication create of autopilot run {RunId} was refused with {ErrorCode}.")]
    private partial void LogCreateRefused(Guid runId, string errorCode);

    [LoggerMessage(
        EventId = 6573,
        Level = LogLevel.Information,
        Message = "Autopilot run {RunId} adopted the episode's live publication {PublicationId} instead of creating a second.")]
    private partial void LogAdopted(Guid runId, Guid publicationId);
}
