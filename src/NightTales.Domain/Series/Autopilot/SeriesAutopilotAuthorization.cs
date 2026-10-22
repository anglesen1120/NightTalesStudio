using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// One accepted version of the autopilot authorization text of a series (00 overview §6
/// <c>series_autopilot_authorizations</c>). Shell owned by 09.2.2-T1; the behaviour is 02.8.3-T2's.
/// </summary>
/// <remarks>
/// <para>
/// A row is accepted (<see cref="Accept"/>) and later revoked (<see cref="Revoke"/>); it is never edited
/// afterwards. Revoking keeps the row, because the audit and the plan §3.7 record are the history of what the
/// owner agreed to — the series only drops its pointer (<c>series.current_autopilot_authorization_id</c>).
/// </para>
/// <para>
/// The stored <see cref="Cadence"/> is the <em>covered snapshot</em> of plan §3.7 (the settings fields the
/// accepted text names), written by <see cref="AutopilotAuthorizationCoverage.SnapshotJson"/> and diffed by
/// <see cref="AutopilotAuthorizationCoverage.FirstCoveredChange"/>. It is deliberately not the whole settings
/// document: a change to a field the text never names must not revoke.
/// </para>
/// </remarks>
public sealed class SeriesAutopilotAuthorization : Entity, IUserOwned, IAuditable
{
    private SeriesAutopilotAuthorization()
    {
    }

    public Guid UserId { get; private set; }

    public Guid SeriesId { get; private set; }

    public int Version { get; private set; }

    public string TextVersion { get; private set; } = string.Empty;

    /// <summary>SHA-256 hex of the rendered text (<c>char(64)</c>).</summary>
    public string TextHash { get; private set; } = string.Empty;

    public Topic Topic { get; private set; }

    /// <summary>Placeholder FK to <c>channel_connections</c>, whose constraint 06 adds.</summary>
    public Guid? ChannelConnectionId { get; private set; }

    /// <summary>The covered snapshot of the settings the accepted text named (see the type remarks).</summary>
    public string Cadence { get; private set; } = string.Empty;

    public PrivacyStatus PrivacyStatus { get; private set; }

    public DateTimeOffset AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>
    /// Why the row stopped being current (plan §3.7); stored as the snake_case wire value of
    /// <see cref="AutopilotAuthorizationRevokeReason"/> and <see langword="null"/> while the row is live.
    /// </summary>
    public AutopilotAuthorizationRevokeReason? RevokedReason { get; private set; }

    /// <summary>Whether the row was revoked, i.e. whether <see cref="RevokedAt"/> is set.</summary>
    public bool IsRevoked => RevokedAt is not null;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Accepts a new authorization version for a series (02.8.3 plan §3.7): what the owner agreed to is fixed
    /// at this instant and never re-derived from the series afterwards.
    /// </summary>
    /// <remarks>
    /// The caller has already rendered the text and matched its hash against what the owner saw
    /// (<c>AcceptAutopilotAuthorizationCommand</c>); this factory only refuses a malformed row, because none
    /// of its conditions can be repaired by a client retry: a hash that is not a lowercase SHA-256 hex digest
    /// is a caller bug, not a stale text (that one answers 409 <c>autopilot_authorization_required</c> first).
    /// The version the caller passes is <c>previous + 1</c>, read inside the accept transaction.
    /// </remarks>
    /// <param name="userId">The owner the row belongs to and the tenant filter compares (09 README D10).</param>
    /// <param name="seriesId">The series the authorization is for; part of the same-owner foreign key.</param>
    /// <param name="version">The 1-based version of this acceptance (the <c>previous + 1</c> of the accept).</param>
    /// <param name="textVersion">The template version the text was rendered from, e.g. <c>autopilot-auth.v1</c>.</param>
    /// <param name="textHash">The SHA-256 hex of the text the owner saw, lowercase.</param>
    /// <param name="topic">The series topic at acceptance (a covered field).</param>
    /// <param name="channelConnectionId">The channel connection the text named (a covered field).</param>
    /// <param name="snapshotJson">The covered snapshot of the settings, from <see cref="AutopilotAuthorizationCoverage.SnapshotJson"/>.</param>
    /// <param name="privacy">The publication privacy the authorization covers.</param>
    /// <param name="now">The command's clock reading; the domain never reads the clock itself.</param>
    /// <returns>The new row, tracked by the caller's context.</returns>
    /// <exception cref="ArgumentException">
    /// An id is empty, or a text member is empty, or <paramref name="textHash"/> is not 64 lowercase hex.
    /// </exception>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is below 1.</exception>
    public static SeriesAutopilotAuthorization Accept(
        Guid userId,
        Guid seriesId,
        int version,
        string textVersion,
        string textHash,
        Topic topic,
        Guid channelConnectionId,
        string snapshotJson,
        PrivacyStatus privacy,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("An authorization needs the owner it belongs to.", nameof(userId));
        }

        if (seriesId == Guid.Empty)
        {
            throw new ArgumentException("An authorization needs the series it is for.", nameof(seriesId));
        }

        if (channelConnectionId == Guid.Empty)
        {
            throw new ArgumentException("An authorization names the channel connection its text names.", nameof(channelConnectionId));
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "An authorization version is 1-based.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(textVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(textHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);

        if (!IsLowercaseSha256Hex(textHash))
        {
            throw new ArgumentException("A text hash is 64 lowercase hex characters.", nameof(textHash));
        }

        return new SeriesAutopilotAuthorization
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            SeriesId = seriesId,
            Version = version,
            TextVersion = textVersion,
            TextHash = textHash,
            Topic = topic,
            ChannelConnectionId = channelConnectionId,
            Cadence = snapshotJson,
            PrivacyStatus = privacy,
            AcceptedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Revokes the row for <paramref name="reason"/> (plan §3.7), leaving every other column as it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent: the first reason wins, because it is the trigger that actually happened first, and a second
    /// caller (the settings path and the archive path of one command, say) must not rewrite the history. The
    /// caller that gets <see langword="false"/> still runs its own side effects (the audit row, the runs moved
    /// to <c>awaiting_authorization</c>): those are per request, not per row change.
    /// </para>
    /// <para>
    /// Those run moves are the command's, and they are narrower than "every non-terminal run"
    /// (S77 contract D-3): a revoke moves a run to <c>awaiting_authorization</c> only where
    /// <see cref="AutopilotRunStateMachine.CanTransition"/> allows it — from <c>planned</c> — because a run that
    /// is already producing has a rendered asset and a slot to account for. The other non-terminal runs stay
    /// where they are and pause with <see cref="AutopilotPauseReason.OwnerPaused"/> at the next spend check
    /// (the T1 behaviour of <c>AutopilotStepSupport</c>). That is a deliberate deviation from the plan's
    /// "all non-terminal runs" wording, and this remark is where it is recorded.
    /// </para>
    /// </remarks>
    /// <param name="reason">The trigger, from the six wire reasons of plan §3.7.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><see langword="true"/> when this call revoked the row, <see langword="false"/> when it already was.</returns>
    public bool Revoke(AutopilotAuthorizationRevokeReason reason, DateTimeOffset now)
    {
        if (IsRevoked)
        {
            return false;
        }

        RevokedAt = now;
        RevokedReason = reason;
        UpdatedAt = now;

        return true;
    }

    private static bool IsLowercaseSha256Hex(string value) =>
        value.Length == 64 && value.All(static character => char.IsAsciiHexDigitLower(character));
}
