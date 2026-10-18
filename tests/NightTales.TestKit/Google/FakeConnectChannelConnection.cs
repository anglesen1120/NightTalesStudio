using ErrorOr;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Channels;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using DomainEmailMessage = NightTales.Domain.Communication.EmailMessage;

namespace NightTales.TestKit.Google;

/// <summary>What the fake channel hand-off answers with.</summary>
public enum FakeConnectOutcome
{
    /// <summary>The connection is written inside the caller's unit and its id is returned.</summary>
    Connected,

    /// <summary>The platform account is already connected to another user: <c>channel_already_connected</c>.</summary>
    Conflict,
}

/// <summary>
/// The mutable state a test drives <see cref="FakeConnectChannelConnection"/> with, and the record of the
/// hand-offs it received. Held by the test and shared by every scope of the host, so a test can read the
/// calls after the request finished.
/// </summary>
public sealed class FakeConnectChannelState
{
    /// <summary>
    /// The canary <c>state_hash</c> of the marker row the hand-off writes in
    /// <see cref="WriteBeforeAnswering"/> mode: the stand-in for the <c>channel_connections</c> row 06.1.2-T2
    /// will insert, so a rollback assertion is evidence about a row that really existed (R2).
    /// </summary>
    public const string MarkerStateHash = "canary-writer-marker-state-hash";

    /// <summary>The template of the marker e-mail row the hand-off writes in <see cref="WriteBeforeAnswering"/> mode.</summary>
    public const string MarkerEmailTemplate = "canary-writer-marker";

    private readonly List<ConnectChannelConnectionRequest> _calls = [];
    private readonly object _gate = new();

    /// <summary>What the next hand-off answers; <see cref="FakeConnectOutcome.Connected"/> by default.</summary>
    public FakeConnectOutcome Outcome { get; set; } = FakeConnectOutcome.Connected;

    /// <summary>
    /// When true the hand-off queues the connect e-mail through the ambient <see cref="IEmailSender"/>
    /// inside the caller's unit — the shape 06.1.2-T2's real implementation has for T1's tests — and
    /// returns its error, which is how the "queue failure after the claim" case is driven.
    /// </summary>
    public bool QueueConnectEmail { get; set; }

    /// <summary>The template the queued e-mail names when <see cref="QueueConnectEmail"/> is on.</summary>
    public string EmailTemplate { get; set; } = "channel_connected";

    /// <summary>
    /// When true the hand-off writes the rows a real writer would write — the marker row the harness counts
    /// as the connection, and a connect e-mail row with its outbox event — <em>before</em> it answers with
    /// <see cref="Outcome"/> or throws. That is what makes the "failure after the claim leaves no row"
    /// assertions evidence about rolled-back rows instead of about rows nothing ever wrote (R2).
    /// </summary>
    public bool WriteBeforeAnswering { get; set; }

    /// <summary>
    /// When true the hand-off throws after writing its rows, so a test can prove the callback still answers
    /// the fixed <c>302</c> (with the nonce cleared) when the pipeline rolls unit of work #2 back (R2).
    /// </summary>
    public bool Throw { get; set; }

    /// <summary>The connection id a successful hand-off returns.</summary>
    public Guid ConnectionId { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// What the hand-off answers as <c>ConnectChannelOutcome.Reconnected</c> — the value 06.1.2-T1's audit row
    /// records as <c>data.reconnected</c> (06 CR-22). Defaults to <see langword="false"/>, the first-connect
    /// answer.
    /// </summary>
    public bool Reconnected { get; set; }

    /// <summary>Every hand-off the fake received, in arrival order.</summary>
    public IReadOnlyList<ConnectChannelConnectionRequest> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>How many hand-offs arrived: "one connection row" until <c>channel_connections</c> exists (T2).</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    internal void Record(ConnectChannelConnectionRequest request)
    {
        lock (_gate)
        {
            _calls.Add(request);
        }
    }
}

/// <summary>
/// The hand-written stand-in for 06.1.2-T2's <c>IConnectChannelConnection</c> handler (T2 ships the real
/// one), registered in the test host only: it records every call, can answer
/// <c>channel_already_connected</c>, and — like the real handler would — queues the connect e-mail through
/// <see cref="IEmailSender"/> inside the caller's unit of work.
/// </summary>
/// <remarks>
/// <para>
/// With <see cref="FakeConnectChannelState.WriteBeforeAnswering"/> it also writes rows of its own through
/// <see cref="IAppDbContext"/> before it answers or throws, which is what lets a test prove that unit of
/// work #2 is rolled back rather than merely that nothing was written (R2). Two stand-ins are deliberate:
/// <c>channel_connections</c> does not exist until T2, so the connection row is stood in by an
/// <see cref="OAuthState"/> row carrying the canary <see cref="FakeConnectChannelState.MarkerStateHash"/>
/// (a legal tracked entity, owned by no user), and the connect e-mail is written through the domain
/// factory, so it raises its real <c>email_requested.v1</c> outbox event.
/// </para>
/// <para>
/// It never flushes or commits: like the real implementation it must leave everything to the caller's single
/// <c>SaveChangesAsync</c>, which the contract of <see cref="IConnectChannelConnection"/> requires.
/// </para>
/// </remarks>
/// <param name="email">The ambient sender, so the queued e-mail joins the caller's unit of work.</param>
/// <param name="database">The ambient write model, for the rows the hand-off writes itself.</param>
/// <param name="clock">The host's clock, so the marker rows carry the test's instant.</param>
/// <param name="state">The test's switchboard and call log.</param>
public sealed class FakeConnectChannelConnection(
    IEmailSender email,
    IAppDbContext database,
    IClock clock,
    FakeConnectChannelState state)
    : IConnectChannelConnection
{
    /// <summary>
    /// The <c>user_id</c> of the marker row: no user, so the harness's per-user state counts stay about the
    /// connect's own row and <c>oauth_states</c> has no foreign key to satisfy.
    /// </summary>
    private static readonly Guid MarkerUserId = Guid.Empty;

    /// <inheritdoc />
    public async Task<ErrorOr<ConnectChannelOutcome>> ConnectAsync(ConnectChannelConnectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        state.Record(request);

        if (state.WriteBeforeAnswering)
        {
            WriteRows(request.UserId);
        }

        if (state.Throw)
        {
            // Mid-unit failure: the pipeline rolls unit of work #2 back and the endpoint answers its fixed
            // redirect. The message is a canary and never reaches a log line (only the type is logged).
            throw new InvalidOperationException("canary channel-writer failure");
        }

        if (state.QueueConnectEmail)
        {
            var queued = await email
                .QueueAsync(
                    new EmailMessage(request.UserId, state.EmailTemplate, Language.Vi, new Dictionary<string, string>(StringComparer.Ordinal)),
                    ct)
                .ConfigureAwait(false);

            if (queued.IsError)
            {
                return queued.Errors;
            }
        }

        if (state.Outcome == FakeConnectOutcome.Conflict)
        {
            return Error.Conflict(ErrorCodes.ChannelAlreadyConnected);
        }

        return new ConnectChannelOutcome(state.ConnectionId, state.Reconnected);
    }

    /// <summary>
    /// The rows the T2 writer would have written before failing: the connection marker and the connect
    /// e-mail with its outbox event. Both are tracked, and neither this method nor the queue commits.
    /// </summary>
    /// <param name="userId">The account the connection belongs to (the e-mail row's owner).</param>
    private void WriteRows(Guid userId)
    {
        database.OAuthStates.Add(
            OAuthState.Create(
                MarkerUserId,
                Platform.YouTube,
                FakeConnectChannelState.MarkerStateHash,
                "canary-marker-protected-verifier",
                "canary-marker-browser-nonce-hash",
                "canary-marker-oauth-client",
                Guid.CreateVersion7(),
                clock.UtcNow,
                TimeSpan.FromMinutes(10)));

        database.EmailMessages.Add(
            DomainEmailMessage.Queue(
                userId,
                FakeConnectChannelState.MarkerEmailTemplate,
                Language.Vi,
                "canary-writer-marker-to-hash",
                "{}",
                clock.UtcNow));
    }
}
