using ErrorOr;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Channels;
using NightTales.Domain.Common;

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

    /// <summary>The connection id a successful hand-off returns.</summary>
    public Guid ConnectionId { get; set; } = Guid.CreateVersion7();

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
/// <see cref="IEmailSender"/> inside the caller's unit of work. A failed hand-off queues nothing, so a test
/// can prove that a failure after the claim leaves no audit, outbox or e-mail row behind.
/// </summary>
/// <param name="email">The ambient sender, so the queued e-mail joins the caller's unit of work.</param>
/// <param name="state">The test's switchboard and call log.</param>
public sealed class FakeConnectChannelConnection(
    IEmailSender email,
    FakeConnectChannelState state)
    : IConnectChannelConnection
{
    /// <inheritdoc />
    public async Task<ErrorOr<Guid>> ConnectAsync(ConnectChannelConnectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        state.Record(request);

        if (state.Outcome == FakeConnectOutcome.Conflict)
        {
            return Error.Conflict(ErrorCodes.ChannelAlreadyConnected);
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

        return state.ConnectionId;
    }
}
