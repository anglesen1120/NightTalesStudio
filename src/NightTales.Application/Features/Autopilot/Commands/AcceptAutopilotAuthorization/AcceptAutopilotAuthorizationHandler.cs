using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Compliance;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Authorization;
using NightTales.Application.Features.Autopilot.Dtos;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Commands.AcceptAutopilotAuthorization;

/// <summary>
/// Records the acceptance of a series' authorization text (02.8.3-T2 task step 5): re-renders both locales from the
/// stored settings, compares the exact digest, inserts the row at <c>previous + 1</c>, repoints the series, revokes
/// what it supersedes and raises the outbox event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order: ownership, then archive, then re-auth, then the text.</b> The tenant-filtered load answers 404 for a
/// foreign series before the re-auth guard can answer 401 — the tenancy contract every consent-bearing route keeps
/// (the <c>ConnectYouTube</c> precedent). An archived series answers 409 <c>series_archived</c> next (S77 review
/// C-3/S-2). Accepting is consent-bearing (§3.7 marks the route re-auth required), so the guard runs after those two,
/// then the render.
/// </para>
/// <para>
/// <b>Rendering is the comparison.</b> The two shipped locales are rendered from the <em>stored</em> settings, topic,
/// channel title and audit state; the request matches when one of them carries the same template version and the same
/// digest. A stale page therefore cannot be accepted — the settings moved under it — and the refusal names which of
/// the two values was stale (<c>errors.textHash</c> / <c>errors.textVersion</c>, plan §3.7), never the text.
/// </para>
/// <para>
/// <b>Accepting supersedes.</b> A previous current row is revoked <c>superseded</c> in the same transaction, so the
/// series never has two live authorizations; the aggregate raises <c>AutopilotAuthorizationAccepted</c>, which the
/// outbox routes (S71-7: an acceptance does not advance a run by itself).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="currentUser">The owner the row is written for.</param>
/// <param name="clock">The clock the acceptance is stamped with.</param>
/// <param name="legal">The 13.2.1 legal gate, the ladder's first rung on this consent-bearing route too.</param>
/// <param name="reauth">The recent-re-authentication gate of the consent-bearing route.</param>
/// <param name="auditPolicy">The channel's audit state, part of the text and of the snapshot.</param>
/// <param name="renderer">The single render point of the authorization text.</param>
/// <param name="audit">The writer of the supersede row, inside the same transaction.</param>
public sealed class AcceptAutopilotAuthorizationHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    ILegalAcceptance legal,
    IReauthGuard reauth,
    IChannelAuditPolicy auditPolicy,
    AuthorizationTextRenderer renderer,
    IAuditWriter audit) : ICommandHandler<AcceptAutopilotAuthorizationCommand, ErrorOr<AcceptedAuthorizationDto>>
{
    /// <summary>The re-auth window of the consent-bearing route (BD4: five minutes).</summary>
    public static readonly TimeSpan ReauthWindow = TimeSpan.FromMinutes(5);

    /// <summary>The metadata key the writer turns into <c>errors.textHash</c> / <c>errors.textVersion</c>.</summary>
    private const string FieldMetadata = "field";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<AcceptedAuthorizationDto>> Handle(
        AcceptAutopilotAuthorizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (series is null)
        {
            return Errors.NotFound();
        }

        // S77 review C-3/S-2: an archived series may never carry a live consent pointer again — its autopilot is off
        // and nothing may produce for it, so an acceptance would only record consent to uploads that cannot happen.
        // The refusal is above the consent gates for the same reason the enable ladder's is: no legal or re-auth
        // question is worth asking on a series that cannot be enabled.
        if (series.ArchivedAt is not null)
        {
            return Error.Conflict(
                ErrorCodes.SeriesArchived,
                "The series is archived; its autopilot authorization cannot be accepted.");
        }

        // The ladder is the contract's (contract line 60): legal → re-auth → settings → render. The legal acceptance
        // comes first because consenting to autopilot uploads presupposes the current privacy/ToS documents; its error
        // is returned untouched (13.2.1-T1).
        var legalCurrent = await legal.EnsureCurrentAsync(series.UserId, cancellationToken).ConfigureAwait(false);
        if (legalCurrent.IsError)
        {
            return legalCurrent.Errors;
        }

        var recent = await reauth.RequireRecentAsync(ReauthWindow, cancellationToken).ConfigureAwait(false);
        if (recent.IsError)
        {
            return recent.Errors;
        }

        var settings = AutopilotSettingsValidator.Validate(series.AutopilotSettings);
        if (settings.IsError)
        {
            return settings.Errors;
        }

        if (series.DefaultChannelId is not { } channelId)
        {
            return Error.Conflict(
                ErrorCodes.AutopilotAuthorizationRequired,
                "The series has no channel, so there is no text to accept.");
        }

        var channel = await db.ChannelConnections
            .SingleOrDefaultAsync(candidate => candidate.Id == channelId, cancellationToken)
            .ConfigureAwait(false);

        if (channel is null)
        {
            return Error.Conflict(
                ErrorCodes.AutopilotAuthorizationRequired,
                "The series' channel is gone, so there is no text to accept.");
        }

        var preAuditPending = await auditPolicy.IsPreAuditAsync(channel.Id, cancellationToken).ConfigureAwait(false);
        var match = Render(series, channel.DisplayName ?? string.Empty, settings.Value, preAuditPending, command);
        if (match.IsError)
        {
            return match.Errors;
        }

        var rendered = match.Value;
        var now = clock.UtcNow;

        var previous = await LoadCurrentAsync(series, cancellationToken).ConfigureAwait(false);

        // A replay of an identical acceptance is not a new version (contract line 60): when the live row already carries
        // this digest and its snapshot still covers the stored settings, the existing acceptance is returned unchanged,
        // with no row insert, no supersede and no second audit row.
        if (previous is not null
            && string.Equals(previous.TextHash, rendered.TextHash, StringComparison.Ordinal)
            && string.Equals(previous.TextVersion, rendered.TextVersion, StringComparison.Ordinal)
            && AutopilotAuthorizationCoverage.FirstCoveredChange(
                previous.Cadence,
                previous.Topic,
                previous.ChannelConnectionId,
                series.Topic,
                series.DefaultChannelId,
                settings.Value,
                preAuditPending) is null)
        {
            return new AcceptedAuthorizationDto(previous.Version, previous.AcceptedAt);
        }

        var nextVersion = await NextVersionAsync(series.Id, cancellationToken).ConfigureAwait(false);

        var row = SeriesAutopilotAuthorization.Accept(
            userId: series.UserId,
            seriesId: series.Id,
            version: nextVersion,
            textVersion: rendered.TextVersion,
            textHash: rendered.TextHash,
            topic: series.Topic,
            channelConnectionId: channel.Id,
            snapshotJson: AutopilotAuthorizationCoverage.SnapshotJson(settings.Value, preAuditPending),
            privacy: settings.Value.Privacy,
            now: now);

        db.SeriesAutopilotAuthorizations.Add(row);
        series.AcceptAutopilotAuthorization(row, now);

        if (previous is not null && previous.Revoke(AutopilotAuthorizationRevokeReason.Superseded, now))
        {
            await audit.WriteAsync(
                new AuditEntry(
                    ActorUserId: currentUser.Id,
                    ActorType: currentUser.Actor == ActorType.System ? ActorType.System : ActorType.User,
                    SubjectUserId: currentUser.Id,
                    Action: "autopilot.authorization_revoked",
                    Category: Category.Publishing,
                    Visibility: Visibility.User,
                    EntityType: "series",
                    EntityId: series.Id,
                    DataJson: System.Text.Json.JsonSerializer.Serialize(
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["seriesId"] = series.Id,
                            ["authorizationId"] = previous.Id,
                            ["reason"] = AutopilotAuthorizationRevokeReason.Superseded.ToWire(),
                        }),
                    Ip: null,
                    UserAgent: null,
                    ActorLabel: null,
                    OccurredAt: now),
                cancellationToken).ConfigureAwait(false);
        }

        // The projection the pipeline's audit behaviour reads: the ids only exist now.
        command.AcceptedAuthorizationId = row.Id;
        command.AcceptedVersion = row.Version;

        return new AcceptedAuthorizationDto(row.Version, row.AcceptedAt);
    }

    /// <summary>
    /// Renders both shipped locales from the stored state and returns the one whose version and digest the request
    /// names, or the 409 that says which value was stale (plan §3.7).
    /// </summary>
    /// <param name="series">The tracked series.</param>
    /// <param name="channelTitle">The channel's display title, part of the sentence.</param>
    /// <param name="settings">The stored settings.</param>
    /// <param name="preAuditPending">Whether the channel's audit is pending.</param>
    /// <param name="command">The request whose pair is compared.</param>
    /// <returns>The matched render, or the refusal.</returns>
    private ErrorOr<RenderedAuthorizationText> Render(
        SeriesAggregate series,
        string channelTitle,
        AutopilotSettings settings,
        bool preAuditPending,
        AcceptAutopilotAuthorizationCommand command)
    {
        var versionMatched = false;

        foreach (var locale in AuthorizationTextRenderer.Locales)
        {
            var rendered = renderer.Render(new AuthorizationTextRequest(
                locale,
                series.Title,
                series.Topic,
                channelTitle,
                settings,
                preAuditPending));

            if (rendered.IsError)
            {
                return rendered.Errors;
            }

            var value = rendered.Value;
            if (!string.Equals(value.TextVersion, command.TextVersion, StringComparison.Ordinal))
            {
                continue;
            }

            versionMatched = true;
            if (string.Equals(value.TextHash, command.TextHash, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return versionMatched
            ? Stale("textHash", "The text changed since it was displayed; read it again and accept it.")
            : Stale("textVersion", "The authorization text has a new version; read it again and accept it.");
    }

    /// <summary>The 409 whose <c>errors</c> member names the stale field (plan §3.7).</summary>
    /// <remarks>
    /// The metadata carries only the field: the writer maps <c>textHash</c> → <c>stale_text</c> and
    /// <c>textVersion</c> → <c>stale_version</c> from an allow-list of its own, so no free text has to travel to
    /// the response.
    /// </remarks>
    /// <param name="field">The wire field, <c>textHash</c> or <c>textVersion</c>.</param>
    /// <param name="description">The client-safe sentence.</param>
    /// <returns>The conflict.</returns>
    private static Error Stale(string field, string description) =>
        Error.Conflict(
            code: ErrorCodes.AutopilotAuthorizationRequired,
            description: description,
            metadata: new Dictionary<string, object> { [FieldMetadata] = field });

    /// <summary>The series' current, live authorization row, or <see langword="null"/>.</summary>
    /// <param name="series">The series whose pointer is read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    private async Task<SeriesAutopilotAuthorization?> LoadCurrentAsync(SeriesAggregate series, CancellationToken ct)
    {
        if (series.CurrentAutopilotAuthorizationId is not { } id)
        {
            return null;
        }

        var row = await db.SeriesAutopilotAuthorizations
            .SingleOrDefaultAsync(candidate => candidate.Id == id, ct)
            .ConfigureAwait(false);

        return row is { IsRevoked: false } ? row : null;
    }

    /// <summary>The version the new row takes: the highest the series ever recorded, plus one.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns><c>previous + 1</c>, or <c>1</c> for the first acceptance.</returns>
    private async Task<int> NextVersionAsync(Guid seriesId, CancellationToken ct)
    {
        var highest = await db.SeriesAutopilotAuthorizations
            .Where(row => row.SeriesId == seriesId)
            .OrderByDescending(row => row.Version)
            .Select(row => (int?)row.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return (highest ?? 0) + 1;
    }
}
