using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Commands.CreatePublication;

/// <summary>
/// Creates an episode's publication in <c>awaiting_consent</c> (06.2.1-T1 task §Application steps 1-10, 06 README
/// D6/D11/D12/D13/D17): reads the episode, the connection and the render's music attribution, refuses a metadata
/// set YouTube would reject, resolves the veto window of an autopilot create, applies the pre-audit privacy
/// override and writes the row, its outbox message and its audit row in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is the contract</b> (steps 1-10). The validation of step 5 runs on the <em>submitted</em> privacy,
/// before the pre-audit override of step 7 rewrites it to <c>private</c>: a submission that says
/// <c>public</c> is checked as <c>public</c>, and only then is the effective value chosen — otherwise a
/// pre-audit client could never be told that the privacy it asked for is not the privacy it will get, and the
/// <c>platform_payload</c> stamp would hide the very value the check had to see.
/// </para>
/// <para>
/// <b>Why the handler flushes.</b> Step 8 saves inside the handler although the pipeline's
/// <c>UnitOfWorkBehavior</c> commits the transaction: the partial unique index
/// <c>ux_publications_active_episode_platform</c> is the arbiter of "one live publication per episode and
/// platform", and only a real flush can surface its refusal. Translating it here — before the audit slot runs —
/// is what makes the loser of a double create answer 409 <c>publication_invalid_state</c> and leave no audit row
/// and no outbox row behind, instead of a 500 from the pipeline's commit.
/// </para>
/// <para>
/// <b>What is never logged.</b> Ids and codes only: never the title, the description or the channel display name
/// (06 README D17). The outbox payload is the aggregate's own event, which carries ids, the status and the error
/// code for the same reason.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the tenant filter is what turns "another owner's row" into a 404.</param>
/// <param name="options">The <c>YouTube</c> section (06 README D11): the audit status, the publish lead time and the minimum veto window.</param>
/// <param name="clock">The scope's clock; one instant is read for the whole command.</param>
/// <param name="logger">The id-only log lines of this path.</param>
public sealed partial class CreatePublicationHandler(
    IAppDbContext db,
    YouTubeOptions options,
    IClock clock,
    ILogger<CreatePublicationHandler> logger)
    : ICommandHandler<CreatePublicationCommand, ErrorOr<PublicationDto>>
{
    /// <summary>The request field a veto-window refusal answers on (the task file's <c>publishAt</c> row).</summary>
    private const string PublishAtField = "publishAt";

    /// <summary>The request field a revoked-autorization refusal answers on (command-only, never a body member).</summary>
    private const string AutopilotAuthorizationField = "autopilotAuthorizationId";

    /// <summary>The key of the veto window inside <c>series.autopilot_settings</c> (02-owned, read-only here).</summary>
    private const string VetoWindowHoursKey = "veto_window_hours";

    /// <summary>The veto window of a series that never chose one (02 D-x default, hours).</summary>
    private const int DefaultVetoWindowHours = 12;

    /// <summary>The smallest window the formula may leave (YR1, hours).</summary>
    private const int MinVetoWindowHours = 2;

    /// <summary>The largest window the formula may leave (YR1, hours).</summary>
    private const int MaxVetoWindowHours = 48;

    /// <inheritdoc />
    public async ValueTask<ErrorOr<PublicationDto>> Handle(
        CreatePublicationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = clock.UtcNow;

        // Step 1: the episode, owner-scoped. A foreign or unknown id takes the same 404 (09 README D10).
        var episode = await db.Episodes
            .SingleOrDefaultAsync(row => row.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);

        if (episode is null)
        {
            return Errors.NotFound();
        }

        // Step 2: only a ready episode may be published (09.1.4-T1's episode_invalid_state).
        if (episode.Status != EpisodeStatus.Ready)
        {
            return PublicationErrors.EpisodeInvalidState();
        }

        // Step 3: the connection, owner-scoped as well; a revoked, expired or error row is channel_not_active.
        var connection = await db.ChannelConnections
            .SingleOrDefaultAsync(row => row.Id == command.ChannelConnectionId, cancellationToken)
            .ConfigureAwait(false);

        if (connection is null)
        {
            return Errors.NotFound();
        }

        if (connection.Status != ChannelConnectionStatus.Active)
        {
            return PublicationErrors.ChannelNotActive();
        }

        // Step 4: the render's music attribution, from the latest job of the episode (WT49: UUIDv7 id, not
        // created_at). A missing job or a job without music simply contributes no attribution.
        var renderParams = await db.RenderJobs
            .Where(job => job.EpisodeId == command.EpisodeId)
            .OrderByDescending(job => job.Id)
            .Select(job => job.Params)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var attribution = renderParams?.Music?.Attribution;

        // Step 5: normalize the text, append the attribution, then validate — in that order.
        // YouTubeMetadataRules never rewrites what it is given ("the caller trims and normalizes before it
        // calls", the CreateEpisodeHandler precedent), so the counts it applies are the counts of exactly the
        // text this command stores: a title is measured trimmed and stored trimmed, and a tag is either
        // non-blank after trimming or refused as a tag.
        var title = command.Title.Trim();
        // A JSON null element deserializes to a null string: treat it as blank so the domain refuses it (400), never an NRE (500).
        var tags = command.Tags.Select(tag => (tag ?? string.Empty).Trim()).ToList();
        var description = YouTubeMetadataRules.AppendAttribution(command.Description.Trim(), attribution);

        var valid = YouTubeMetadataRules.Validate(
            title,
            description,
            tags,
            command.Privacy,
            command.PublishAt,
            now,
            options.PublishLeadMinutes);

        if (valid.IsError)
        {
            return valid.Errors;
        }

        // Step 6: the autopilot branch — a revoked authorization and YR1's veto window.
        DateTimeOffset? vetoEndsAt = null;

        if (command.AutopilotAuthorizationId is { } authorizationId)
        {
            var authorizationExists = await db.SeriesAutopilotAuthorizations
                .AnyAsync(
                    row => row.Id == authorizationId && row.RevokedAt == null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!authorizationExists)
            {
                return Errors.ValidationFailed(
                    AutopilotAuthorizationField,
                    "The autopilot authorization does not exist or was revoked.");
            }

            var settings = await db.Series
                .Where(row => row.Id == episode.SeriesId)
                .Select(row => row.AutopilotSettings)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var vetoWindow = TimeSpan.FromHours(VetoWindowHours(settings));
            var byWindow = now.Add(vetoWindow);
            var byUpload = (command.PublishAt ?? now).AddMinutes(-options.PublishLeadMinutes);
            var vetoEnd = byWindow < byUpload ? byWindow : byUpload;

            if (vetoEnd < now.AddHours(options.MinVetoWindowHours))
            {
                return Errors.ValidationFailed(
                    PublishAtField,
                    $"An autopilot publication needs at least {options.MinVetoWindowHours} hours of veto window "
                    + "before its upload starts; schedule it later.");
            }

            vetoEndsAt = vetoEnd;
        }

        // Step 7: the pre-audit privacy override (D12/C4). The submitted value is kept in platform_payload so
        // 06.2.2-T2 can release it once Google approves the client; the row itself is stored private.
        var preAudit = !string.Equals(options.AuditStatus, YouTubeAuditStatuses.Approved, StringComparison.Ordinal);
        var privacy = preAudit ? PublicationPrivacy.Private : command.Privacy;
        var platformPayload = preAudit ? PublicationPlatformPayload.WritePreAudit(command.Privacy) : null;

        // Step 8: the aggregate, then the flush that lets the partial unique index arbitrate.
        var created = Publication.Create(
            episode.UserId,
            episode.Id,
            connection.Id,
            new PublicationMetadata(title, description, tags, privacy, command.PublishAt),
            command.AutopilotAuthorizationId,
            vetoEndsAt,
            platformPayload,
            now);

        if (created.IsError)
        {
            return created.Errors;
        }

        var publication = created.Value;
        db.Publications.Add(publication);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (CreatePublicationConflict.IsActivePublicationViolation(exception))
        {
            // The episode already has a live publication for this platform: the second create is the same 409
            // the wrong-status read answers (06 CR-9), and the rollback of UnitOfWorkBehavior leaves no audit row.
            LogDuplicateActiveEpisode(episode.Id);
            return PublicationErrors.InvalidState();
        }
        catch (DbUpdateException exception) when (CreatePublicationConflict.IsEpisodeForeignKeyViolation(exception))
        {
            // The episode was deleted between the read and the insert: the 404 the read would have answered.
            return Errors.NotFound();
        }

        // Step 9: the audit row's ids (the pipeline's AuditBehavior writes the row on the success path).
        command.Outcome = new PublicationCreatedOutcome(publication.Id, episode.Id, connection.Id);

        LogCreated(publication.Id, episode.Id);

        return PublicationDtoMapper.ToDto(publication, connection.DisplayName);
    }

    /// <summary>
    /// The veto window of the series, in hours, clamped into <c>[MinVetoWindowHours, MaxVetoWindowHours]</c>
    /// (YR1). <c>series.autopilot_settings</c> is 02's document, so absent, malformed or out-of-range content
    /// falls back to the default instead of failing a create the owner asked for (TL ruling #3).
    /// </summary>
    private static int VetoWindowHours(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings))
        {
            return DefaultVetoWindowHours;
        }

        try
        {
            using var document = JsonDocument.Parse(settings);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(VetoWindowHoursKey, out var window)
                || window.ValueKind != JsonValueKind.Number
                || !window.TryGetInt32(out var hours))
            {
                return DefaultVetoWindowHours;
            }

            return Math.Clamp(hours, MinVetoWindowHours, MaxVetoWindowHours);
        }
        catch (JsonException)
        {
            return DefaultVetoWindowHours;
        }
    }

    [LoggerMessage(
        EventId = 6426,
        Level = LogLevel.Information,
        Message = "Publication {PublicationId} of episode {EpisodeId} was created in awaiting_consent.")]
    private partial void LogCreated(Guid publicationId, Guid episodeId);

    [LoggerMessage(
        EventId = 6427,
        Level = LogLevel.Information,
        Message = "A publication create for episode {EpisodeId} was refused: the episode already has a live publication.")]
    private partial void LogDuplicateActiveEpisode(Guid episodeId);
}
