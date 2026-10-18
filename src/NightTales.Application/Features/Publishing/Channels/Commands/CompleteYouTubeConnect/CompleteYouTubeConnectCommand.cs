using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect;

/// <summary>
/// <c>GET /api/v1/channels/youtube/callback?code&amp;state</c> (06 README D3): the anonymous half of the
/// connect flow, which authenticates itself by atomically claiming the state row and answering a fixed
/// <c>302</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AllowAnonymousRequestAttribute"/> is the D4 marker and it is the point of the design: the
/// callback carries no bearer token, so the claimed state <em>and</em> the <c>__Host-nts_oauth</c> cookie
/// are its only authentication. The command therefore carries the raw cookie value
/// (<see cref="BrowserNonce"/>, added by the S58 shared contract because the task file's signature
/// omitted it) instead of reading the request itself.
/// </para>
/// <para>
/// It is <see cref="NotAuditedAttribute"/> on purpose: pipeline slot 6 would write a row for every
/// outcome, including the four failures, while the design records
/// <c>publishing.channel_connected</c> once and only when a channel really changed (06.1.2-T1 task
/// §Handlers step 10).
/// </para>
/// <para>
/// <b>The response is <c>ErrorOr&lt;ConnectYouTubeOutcome&gt;</c> although the outcome is
/// success-typed.</b> The task file and the S58 contract show a bare enum, but every Mediator behaviour
/// in this repo is constrained to <c>TResponse : IErrorOr</c> (<c>MediatorPipeline</c>, and every command
/// in Application answers an <c>ErrorOr</c> — e.g. <c>RefreshCommand : ICommand&lt;ErrorOr&lt;RefreshResult&gt;&gt;</c>),
/// so a bare enum would get no pipeline at all: no <c>UnitOfWorkBehavior</c>, hence no transaction for
/// either boundary, and the claim's commit would throw. S58 slice B deviation, recorded in the task
/// Notes; the handler itself answers an envelope error on one path only — a failed unit #1 claim commit
/// (<c>EfUnitOfWork.CommitAsync</c> mapped a refusal), where there is nothing left to undo — and the
/// pipeline's own commit mapping can do the same after the handler returns.
/// </para>
/// </remarks>
/// <param name="Code">Google's authorization code, or <see langword="null"/> when the user denied consent.</param>
/// <param name="State">The opaque state Google echoed; never logged.</param>
/// <param name="Error">Google's <c>error</c> parameter (<c>access_denied</c> on a refusal).</param>
/// <param name="BrowserNonce">The raw <c>__Host-nts_oauth</c> cookie value, or <see langword="null"/> when the request carried none.</param>
/// <param name="Info">The caller metadata the audit row records.</param>
[AllowAnonymousRequest]
[NotAudited(
    "The callback writes publishing.channel_connected itself through IAuditWriter, once, and only on the "
    + "Connected outcome; slot 6 would also audit the four failure outcomes.")]
public sealed record CompleteYouTubeConnectCommand(
    string? Code,
    string? State,
    string? Error,
    string? BrowserNonce,
    RequestInfo Info)
    : ICommand<ErrorOr<ConnectYouTubeOutcome>>;

/// <summary>
/// What one callback did (06 README D3): the endpoint maps each member to one constant <c>302</c> target
/// and nothing else reaches the <c>Location</c> header.
/// </summary>
public enum ConnectYouTubeOutcome
{
    /// <summary>The channel is connected (or reconnected): <c>/channels?connected=youtube</c>.</summary>
    Connected,

    /// <summary>
    /// Unknown, spent, expired or foreign state, a missing or foreign browser nonce, a dead session family
    /// and an unreadable verifier cipher all answer this one member, so the callback is no oracle
    /// (<c>oauth_state_invalid</c>).
    /// </summary>
    StateInvalid,

    /// <summary>The user refused consent at Google (<c>oauth_denied</c>); the state is already spent.</summary>
    Denied,

    /// <summary>
    /// The exchange or the channel read failed, the pinned client is no longer resolvable, or the connect
    /// writer is missing (<c>oauth_exchange_failed</c>). The code is never retried.
    /// </summary>
    ExchangeFailed,

    /// <summary>This account already has a live connection for that channel (<c>channel_already_connected</c>).</summary>
    ChannelAlreadyConnected,
}
