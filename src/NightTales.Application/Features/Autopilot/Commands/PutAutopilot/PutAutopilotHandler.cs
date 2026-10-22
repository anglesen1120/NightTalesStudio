using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Compliance;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Common;
using NightTales.Application.Features.Autopilot.Authorization;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Commands.PutAutopilot;

/// <summary>
/// Applies <c>PUT /series/{id}/autopilot</c> (02.8.3-T2 task step 4): validate the document, then enable, disable, or
/// change the settings of an enabled series under the plan §3.7 rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>The answer order is the contract's</b> (task §Failure handling). Two of its rungs are answered before this
/// handler runs: the feature filter (404 <c>feature_disabled</c>) and authentication (401) come first, and the
/// endpoint reads and size-checks the body before the command is dispatched, so 413 / 415 / 400 shape errors can
/// precede any answer below (S77 review S-3 — the endpoint, not the handler, owns those). Inside the handler:
/// 404 (foreign or missing, D10) → 412 (stale <c>If-Match</c>) → [enable only] 409 <c>series_archived</c> → 400
/// <c>autopilot_settings_invalid</c> (the document, every failure keyed by JSON path) → the enable ladder → success.
/// Nothing about the row is revealed to a caller who did not read it.
/// </para>
/// <para>
/// <b>Enable ladder</b> (plan §3.7, in this order): (0) the owner's current legal acceptance, whose error is returned
/// untouched (13.2.1-T1); (1) re-auth within five minutes, because this click authorizes future uploads without
/// per-video consent (09.3.2); (2) the settings document; (3) a live authorization whose covered snapshot still
/// matches the <em>new</em> settings, the topic and the channel — compared field by field, never by diffing the
/// rendered text, because the text also carries the series title and a rename must not revoke (plan §3.7); (4) an
/// <c>active</c> channel connection that is not TikTok (07 CR-5).
/// </para>
/// <para>
/// <b>An archived series is never enabled</b> (S77 review C-3/S-2): the refusal sits above the ladder, on the enable
/// branch only, because an archived series must never reach a consent-bearing step — and because
/// <see cref="SeriesAggregate.Archive"/> already switched autopilot off, a re-enable would only re-create a live
/// consent pointer on a series nothing may produce for. Disabling, revoking and a settings edit with
/// <c>enabled = false</c> stay allowed: they reduce risk, and the document of a switched-off series stays editable.
/// </para>
/// <para>
/// <b>Settings-only is not the ladder.</b> The owner is editing the document the text covers, so a covered-field
/// change revokes the authorization (<c>text_changed</c>, or the specific reason the comparator names), clears the
/// pointer and holds the series' open runs; the request still answers 200, which is the acceptance row of the task
/// file. The rule does not depend on the switch: a document sent with <c>enabled = false</c> to a series that is
/// already off is an edit too, so it applies, spends a consent only through the covered-field comparator, and is never
/// recorded as a second switch-off (plan §3.7). Disabling from on revokes with <c>disabled</c> and needs no re-auth:
/// it reduces risk.
/// </para>
/// <para>
/// <b>Stored form.</b> The validated document is re-serialized by <see cref="AutopilotSettingsValidator.Serialize"/>
/// and that canonical text is what the row keeps — the same text the snapshot is taken from, so a client cannot make
/// two identical settings store differently and the covered comparison stays byte-stable.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="currentUser">The owner the rows and the chained commands are written as.</param>
/// <param name="clock">One clock reading for every stamp of the command.</param>
/// <param name="legal">The 13.2.1 legal gate, checked before an enable and never re-written here.</param>
/// <param name="reauth">The recent-re-authentication gate of the consent-bearing enable.</param>
/// <param name="auditPolicy">The channel's YouTube audit state, which the snapshot records.</param>
/// <param name="audit">The writer of the rows this command owns (see the command's <c>[NotAudited]</c> reason).</param>
/// <param name="logger">The change log of this command (ids and slugs only).</param>
public sealed partial class PutAutopilotHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    ILegalAcceptance legal,
    IReauthGuard reauth,
    IChannelAuditPolicy auditPolicy,
    IAuditWriter audit,
    ILogger<PutAutopilotHandler> logger) : ICommandHandler<PutAutopilotCommand, ErrorOr<Success>>
{
    /// <summary>The re-auth window of the enable path (BD4: five minutes).</summary>
    public static readonly TimeSpan ReauthWindow = TimeSpan.FromMinutes(5);

    /// <summary>The projection serializer of the pipeline's audit rows, matched so the stored keys are verbatim.</summary>
    private static readonly JsonSerializerOptions DataSerializer = new() { WriteIndented = false };

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        PutAutopilotCommand command,
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

        if (!ETags.Matches(command.IfMatch, ETags.SeriesAutopilot(series.Version)))
        {
            return Error.Failure(
                ErrorCodes.SeriesConflict,
                "The series changed since it was read; read it again and retry.");
        }

        // S77 review C-3/S-2: an archived series can never be switched on again. The check sits above the enable
        // ladder (an archived series must not reach a consent-bearing step) and outside the other branches (disabling,
        // and the document edit of a series that is already off, stay allowed — they reduce risk and touch no upload).
        if (command.Enabled && series.ArchivedAt is not null)
        {
            return Error.Conflict(
                ErrorCodes.SeriesArchived,
                "The series is archived; autopilot cannot be enabled for it.");
        }

        var now = clock.UtcNow;

        // The enable ladder's order is the contract's (contract line 59, task §"Enable/disable and re-auth"):
        // legal → re-auth → settings valid → authorization matches → channel. The settings document is therefore
        // validated INSIDE the enable branch, after the two consent gates: a caller who has not accepted the legal
        // documents must learn that before anything about the body is judged. The other branches carry no ladder
        // and validate the document first, because the document is applied in every one of them.
        if (!command.Enabled)
        {
            var disabling = AutopilotSettingsValidator.Validate(WithSchemaVersion(command.SettingsJson));
            if (disabling.IsError)
            {
                return disabling.Errors;
            }

            // Plan §3.7: the document travels with the switch in either position. Off sent to a series that is
            // already off is a document edit, not a second switch-off — it writes no `autopilot.disabled` row and
            // never revokes with `disabled`, so a consent the owner gave while the switch was off survives a
            // non-covered edit; only the covered-field comparator can spend it.
            return series.AutopilotEnabled
                ? await DisableAsync(series, disabling.Value, now, cancellationToken).ConfigureAwait(false)
                : await ApplySettingsAsync(series, disabling.Value, now, cancellationToken).ConfigureAwait(false);
        }

        if (series.AutopilotEnabled)
        {
            var changed = AutopilotSettingsValidator.Validate(WithSchemaVersion(command.SettingsJson));
            return changed.IsError
                ? changed.Errors
                : await ApplySettingsAsync(series, changed.Value, now, cancellationToken).ConfigureAwait(false);
        }

        return await EnableAsync(series, command.SettingsJson, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disables autopilot and revokes its authorization with <c>disabled</c> (plan §3.7).</summary>
    /// <param name="series">The tracked, enabled series.</param>
    /// <param name="settings">The validated document of the request; a disable is an edit of the settings too.</param>
    /// <param name="now">The clock reading.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>Always success: a disable cannot be refused.</returns>
    private async ValueTask<ErrorOr<Success>> DisableAsync(
        SeriesAggregate series,
        AutopilotSettings settings,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var current = await LoadCurrentAuthorizationAsync(series, ct).ConfigureAwait(false);

        // The document is applied before the switch, in the order the settings-only path uses: the task's failure row
        // "the settings can still be saved with enabled = false" is about saving the document, not only about the
        // flag, and the canonical text is what the next read serves.
        ApplyCanonicalSettings(series, settings, now);

        var revoked = current is not null && current.Revoke(AutopilotAuthorizationRevokeReason.Disabled, now);

        series.DisableAutopilot(now);

        if (revoked)
        {
            await WriteRevokedAuditAsync(series.Id, current!, now, ct).ConfigureAwait(false);
        }

        await WriteAuditAsync(
            series.Id,
            PutAutopilotAuditKeys.DisabledAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [PutAutopilotAuditKeys.SeriesId] = series.Id,
                [PutAutopilotAuditKeys.RevokedAuthorizationId] = revoked ? current!.Id : null,
                [PutAutopilotAuditKeys.Reason] = revoked
                    ? AutopilotAuthorizationRevokeReason.Disabled.ToWire()
                    : null,
            },
            now,
            ct).ConfigureAwait(false);

        if (revoked)
        {
            await HoldPlannedRunsAsync(series.Id, now, ct).ConfigureAwait(false);
        }

        LogChanged(series.Id, enabled: false, revoked: revoked);

        return Result.Success;
    }

    /// <summary>
    /// Applies the document to the series and revokes only when a covered field moved (plan §3.7).
    /// </summary>
    /// <remarks>
    /// One path for both switch positions: while the series is on this is the settings-only edit, and while it is off
    /// it is the document half of an <c>enabled = false</c> request whose switch was already off. Neither is a
    /// switch-off, so neither writes <c>autopilot.disabled</c>: a covered change revokes with the reason the
    /// comparator names, never with <c>disabled</c>.
    /// </remarks>
    /// <param name="series">The tracked series, enabled or not.</param>
    /// <param name="settings">The validated new settings.</param>
    /// <param name="now">The clock reading.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>Success, or the settings error the aggregate itself refuses with.</returns>
    private async ValueTask<ErrorOr<Success>> ApplySettingsAsync(
        SeriesAggregate series,
        AutopilotSettings settings,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var current = await LoadCurrentAuthorizationAsync(series, ct).ConfigureAwait(false);
        var preAuditPending = await IsPreAuditAsync(series, ct).ConfigureAwait(false);

        // The comparison reads the row's stored snapshot, never the re-rendered text: the text carries the series
        // title, and a rename must not revoke (plan §3.7 [DECISION]).
        var reason = current is null
            ? null
            : AutopilotAuthorizationCoverage.FirstCoveredChange(
                current.Cadence,
                current.Topic,
                current.ChannelConnectionId,
                series.Topic,
                series.DefaultChannelId,
                settings,
                preAuditPending);

        ApplyCanonicalSettings(series, settings, now);

        var revoked = false;
        if (current is not null && reason is { } revokeReason)
        {
            revoked = current.Revoke(revokeReason, now);
            series.SetCurrentAutopilotAuthorization(null, now);

            if (revoked)
            {
                await WriteRevokedAuditAsync(series.Id, current, now, ct).ConfigureAwait(false);
                await HoldPlannedRunsAsync(series.Id, now, ct).ConfigureAwait(false);
            }
        }

        LogChanged(series.Id, enabled: series.AutopilotEnabled, revoked: revoked);

        return Result.Success;
    }

    /// <summary>Runs the enable ladder of plan §3.7 and switches the series on.</summary>
    /// <param name="series">The tracked, disabled series.</param>
    /// <param name="settingsJson">The settings document as the client wrote it, still unvalidated.</param>
    /// <param name="now">The clock reading.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>Success, or the first rung that refused.</returns>
    private async ValueTask<ErrorOr<Success>> EnableAsync(
        SeriesAggregate series,
        string settingsJson,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var owner = currentUser.Id ?? Guid.Empty;

        // (0) The legal gate first: an owner who has not accepted the current documents cannot consent to uploads.
        var legalCurrent = await legal.EnsureCurrentAsync(owner, ct).ConfigureAwait(false);
        if (legalCurrent.IsError)
        {
            return legalCurrent.Errors;
        }

        // (1) A recent re-authentication: enabling authorizes future uploads without a per-video confirmation.
        var recent = await reauth.RequireRecentAsync(ReauthWindow, ct).ConfigureAwait(false);
        if (recent.IsError)
        {
            return recent.Errors;
        }

        // (2) The document. GET hands the settings back without `schema_version` (the task's DTO block has no such
        // member), so a client that PUTs the object it just read would otherwise be refused 400 `schema_version` — a
        // read/write round trip the contract does not allow; the member is supplied when the body omits it, and a
        // stated wrong version is still refused. The validator's errors already carry the code and the JSON path of
        // each failure, so they are returned untouched and the writer groups them under `errors` (plan §3.1).
        var validated = AutopilotSettingsValidator.Validate(WithSchemaVersion(settingsJson));
        if (validated.IsError)
        {
            return validated.Errors;
        }

        var settings = validated.Value;

        // (3) A live authorization whose snapshot still covers the settings being enabled.
        var current = await LoadCurrentAuthorizationAsync(series, ct).ConfigureAwait(false);
        var preAuditPending = await IsPreAuditAsync(series, ct).ConfigureAwait(false);

        if (current is null)
        {
            return Error.Conflict(
                ErrorCodes.AutopilotAuthorizationRequired,
                "Autopilot needs a current authorization before it can be enabled.");
        }

        if (AutopilotAuthorizationCoverage.FirstCoveredChange(
                current.Cadence,
                current.Topic,
                current.ChannelConnectionId,
                series.Topic,
                series.DefaultChannelId,
                settings,
                preAuditPending) is not null)
        {
            return Error.Conflict(
                ErrorCodes.AutopilotAuthorizationRequired,
                "The authorization no longer matches these settings; accept the current text first.");
        }

        // (4) The channel: present, live, and not TikTok, whose Direct Post has no scheduling primitive (07 CR-5).
        var channel = series.DefaultChannelId is { } channelId
            ? await db.ChannelConnections
                .SingleOrDefaultAsync(candidate => candidate.Id == channelId, ct)
                .ConfigureAwait(false)
            : null;

        if (channel is null || channel.Status != ChannelConnectionStatus.Active)
        {
            return Error.Conflict(
                ErrorCodes.ChannelReauthRequired,
                "The default channel is not connected; reconnect it before enabling autopilot.");
        }

        // 07 CR-5: only a platform with a scheduling primitive may be driven by the autopilot. The rule is stated
        // against the wire value rather than against the one member today's enum has, so the day 07 adds `tiktok`
        // the refusal already covers it instead of silently letting it through.
        if (!string.Equals(channel.Platform.ToWire(), NightTales.Domain.Publishing.Platform.YouTube.ToWire(), StringComparison.Ordinal))
        {
            return Error.Conflict(
                ErrorCodes.AutopilotChannelUnsupported,
                "This channel's platform cannot be scheduled by the autopilot.");
        }

        var canonical = ApplyCanonicalSettings(series, settings, now);

        var enabled = series.EnableAutopilot(now);
        if (enabled.IsError)
        {
            return enabled.Errors;
        }

        // S77 review C-2/S-1 follow-up: a run paused while the switch was off (the spend check pauses it with
        // `owner_paused`) is not due, so nothing else would ever release it — and a re-accept cannot do it either while
        // the series is disabled. The ladder above has just proved the live authorization still matches, so this is the
        // same release an acceptance makes, on the other route that restores permission to spend.
        var release = await AutopilotRunHold
            .ReleaseOwnerPausedAsync(db, series.Id, now, ct)
            .ConfigureAwait(false);
        if (release.Refused > 0 || release.Untouched > 0)
        {
            LogRunReleaseIncomplete(series.Id, release.Refused, release.Untouched);
        }

        await WriteAuditAsync(
            series.Id,
            PutAutopilotAuditKeys.EnabledAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [PutAutopilotAuditKeys.SeriesId] = series.Id,
                [PutAutopilotAuditKeys.SettingsHash] = SettingsHash(canonical),
                [PutAutopilotAuditKeys.AuthorizationId] = current.Id,
                [PutAutopilotAuditKeys.AuthorizationVersion] = current.Version,
            },
            now,
            ct).ConfigureAwait(false);

        LogChanged(series.Id, enabled: true, revoked: false);

        return Result.Success;
    }

    /// <summary>The series' current authorization row, or <see langword="null"/> when the pointer is clear or dead.</summary>
    /// <param name="series">The series whose pointer is read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    private async Task<NightTales.Domain.Series.Autopilot.SeriesAutopilotAuthorization?> LoadCurrentAuthorizationAsync(
        SeriesAggregate series,
        CancellationToken ct)
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

    /// <summary>
    /// Returns the settings document with <c>schema_version</c> supplied when the body omitted it (the GET DTO does not
    /// carry the member, so the round trip has to work without it). Any other shape problem is left to the validator.
    /// </summary>
    /// <param name="json">The settings object as the client wrote it.</param>
    /// <returns>The document to validate.</returns>
    private static string WithSchemaVersion(string json)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject document)
            {
                return json;
            }

            if (!document.ContainsKey("schema_version"))
            {
                document["schema_version"] = AutopilotSettingsValidator.SupportedSchemaVersion;
            }

            return document.ToJsonString();
        }
        catch (JsonException)
        {
            // The validator reports an unparseable document itself; this helper must not pre-empt its message.
            return json;
        }
    }

    /// <summary>Whether the channel's YouTube audit is still pending, the flag the snapshot records.</summary>
    /// <param name="series">The series whose default channel is read.</param>
    /// <param name="ct">Cancels the check.</param>
    /// <returns><see langword="true"/> when the audit is pending.</returns>
    private async ValueTask<bool> IsPreAuditAsync(SeriesAggregate series, CancellationToken ct) =>
        series.DefaultChannelId is { } channelId
        && await auditPolicy.IsPreAuditAsync(channelId, ct).ConfigureAwait(false);

    /// <summary>
    /// Holds the series' planned runs through the one shared step (D-3), and logs what the state machine refused and
    /// what the pass bound left for the next one.
    /// </summary>
    /// <param name="seriesId">The series whose planned runs are held.</param>
    /// <param name="now">The clock reading stamped on every moved run.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Nothing.</returns>
    private async Task HoldPlannedRunsAsync(Guid seriesId, DateTimeOffset now, CancellationToken ct)
    {
        var held = await AutopilotRunHold.HoldPlannedAsync(db, seriesId, now, ct).ConfigureAwait(false);
        if (held.Refused > 0)
        {
            LogRunHoldDeferred(seriesId, held.Refused);
        }

        // The bound is above the widest plan the validator accepts, so this only fires for rows a second writer
        // planned into the same window; the next hold of the series rotates them in.
        if (held.Untouched > 0)
        {
            LogRunHoldTruncated(seriesId, held.Untouched);
        }
    }

    /// <summary>
    /// Applies the canonical settings text to the series unless the row already carries it, and returns that text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the comparison.</b> <c>Series.ApplyAutopilotSettings</c> stamps <c>updated_at</c> unconditionally, and
    /// the series row's <c>xmin</c> is its ETag (<c>"sa-&lt;xmin&gt;"</c>, README D9): writing an identical document
    /// would spend the tag the client is holding and make a repeated no-op write look like a change.
    /// </para>
    /// <para>
    /// <b>Why the compare is structural, not ordinal.</b> <c>series.autopilot_settings</c> is <c>jsonb</c>
    /// (<c>SeriesConfiguration</c>), so what a read hands back is PostgreSQL's rendering — keys in its own order,
    /// its own spacing — and an ordinal compare against <c>Serialize</c>'s text would answer "different" for every
    /// row. Two documents are the same when their JSON is the same, member order and whitespace aside.
    /// </para>
    /// <para>
    /// <b>Why it is here and not in the aggregate.</b> The Domain mutator's contract is the state change; keeping the
    /// row untouched when nothing changes is a property of this request, which also owns the audit rows and the
    /// covered-field comparison.
    /// </para>
    /// </remarks>
    /// <param name="series">The tracked series.</param>
    /// <param name="settings">The validated document of the request.</param>
    /// <param name="now">The clock reading.</param>
    /// <returns>The canonical text, whether it was written now or was already stored.</returns>
    private static string ApplyCanonicalSettings(
        SeriesAggregate series,
        AutopilotSettings settings,
        DateTimeOffset now)
    {
        var canonical = AutopilotSettingsValidator.Serialize(settings);

        if (!IsSameDocument(series.AutopilotSettings, canonical))
        {
            series.ApplyAutopilotSettings(canonical, now);
        }

        return canonical;
    }

    /// <summary>Whether the stored document already says exactly what the canonical text says.</summary>
    /// <param name="stored">The row's <c>autopilot_settings</c>, or null when the series never had one.</param>
    /// <param name="canonical">The document <see cref="AutopilotSettingsValidator.Serialize"/> produced.</param>
    /// <returns><see langword="true"/> when the two are the same JSON document.</returns>
    private static bool IsSameDocument(string? stored, string canonical)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        if (string.Equals(stored, canonical, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(stored),
                System.Text.Json.Nodes.JsonNode.Parse(canonical));
        }
        catch (JsonException)
        {
            // A column that does not parse as JSON is never "the same document": the write replaces it.
            return false;
        }
    }

    /// <summary>The lowercase SHA-256 of the canonical settings text the row stored (plan §4).</summary>
    /// <param name="settingsJson">The document as <c>series.autopilot_settings</c> keeps it.</param>
    /// <returns>64 lowercase hex characters.</returns>
    private static string SettingsHash(string settingsJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(settingsJson)));

    /// <summary>Writes the <c>autopilot.authorization_revoked</c> row of plan §4.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="authorization">The revoked row.</param>
    /// <param name="now">The clock reading.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>Nothing; the writer adds the row to the ambient transaction.</returns>
    private Task WriteRevokedAuditAsync(
        Guid seriesId,
        NightTales.Domain.Series.Autopilot.SeriesAutopilotAuthorization authorization,
        DateTimeOffset now,
        CancellationToken ct) =>
        WriteAuditAsync(
            seriesId,
            PutAutopilotAuditKeys.RevokedAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [PutAutopilotAuditKeys.SeriesId] = seriesId,
                [PutAutopilotAuditKeys.AuthorizationId] = authorization.Id,
                [PutAutopilotAuditKeys.Reason] = authorization.RevokedReason?.ToWire(),
            },
            now,
            ct);

    /// <summary>Writes one autopilot audit row of plan §4 with ids and slugs only (D8: no free text).</summary>
    /// <param name="seriesId">The series the row names.</param>
    /// <param name="action">The frozen action key.</param>
    /// <param name="data">The bounded <c>data</c> payload.</param>
    /// <param name="now">The clock reading.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>Nothing; the writer adds the row to the ambient transaction.</returns>
    private Task WriteAuditAsync(
        Guid seriesId,
        string action,
        Dictionary<string, object?> data,
        DateTimeOffset now,
        CancellationToken ct) =>
        audit.WriteAsync(
            new AuditEntry(
                ActorUserId: currentUser.Id,
                ActorType: currentUser.Actor == ActorType.System ? ActorType.System : ActorType.User,
                SubjectUserId: currentUser.Id,
                Action: action,
                Category: AutopilotAuditScope.Category,
                Visibility: AutopilotAuditScope.Visibility,
                EntityType: PutAutopilotAuditKeys.EntityType,
                EntityId: seriesId,
                DataJson: JsonSerializer.Serialize(data, DataSerializer),
                Ip: null,
                UserAgent: null,
                ActorLabel: null,
                OccurredAt: now),
            ct);

    // 02.8.3-T2: 6699-6702 and 6710-6713 are the ids this task takes, clear of `AutopilotRunOrchestrator` /
    // `AutopilotPlanJob` (6690-6694), `PublishYouTubeHandler` (6695-6698, 06.2.3-T2) and 63xx, where
    // `ProposeBibleUpdateHandler` holds 6340-6358 — the collisions `LoggerMessageEventIdTests` caught on this branch.
    /// <summary>Logs one applied change with ids and slugs only (D19).</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="enabled">Whether the series ended enabled.</param>
    /// <param name="revoked">Whether an authorization was revoked on the way.</param>
    [LoggerMessage(EventId = 6710, Level = LogLevel.Information, Message = "Autopilot put: series {seriesId} enabled {enabled}, authorization revoked {revoked}.")]
    private partial void LogChanged(Guid seriesId, bool enabled, bool revoked);

    /// <summary>Logs the planned runs the state machine would not hold; the next tick retries them.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="refused">How many runs the state machine would not move.</param>
    [LoggerMessage(EventId = 6711, Level = LogLevel.Warning, Message = "Autopilot put: {refused} planned run(s) of series {seriesId} could not be held; the next tick retries.")]
    private partial void LogRunHoldDeferred(Guid seriesId, int refused);

    /// <summary>Logs the planned runs one pass did not read, which the next hold of the same series picks up.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="untouched">How many planned runs lie beyond the pass bound.</param>
    [LoggerMessage(EventId = 6713, Level = LogLevel.Warning, Message = "Autopilot put: {untouched} planned run(s) of series {seriesId} lie beyond the hold bound and stay planned; the next hold picks them up.")]
    private partial void LogRunHoldTruncated(Guid seriesId, int untouched);

    /// <summary>Logs the owner-paused runs an enable did not release; the next resume or acceptance retries them.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="refused">How many rows the domain refused to resume.</param>
    /// <param name="untouched">How many paused rows lie beyond the release bound.</param>
    [LoggerMessage(EventId = 6700, Level = LogLevel.Warning, Message = "Autopilot put: enabling series {seriesId} left {refused} paused run(s) unreleased and {untouched} beyond the release bound.")]
    private partial void LogRunReleaseIncomplete(Guid seriesId, int refused, int untouched);
}
