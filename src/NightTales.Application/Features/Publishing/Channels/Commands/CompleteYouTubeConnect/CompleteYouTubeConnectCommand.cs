using System.Globalization;
using System.Text;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect;

/// <summary>
/// <c>GET /api/v1/channels/{platform}/callback?code&amp;state</c> (06 README D3): the anonymous half of the
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
/// <see cref="Platform"/> is the <em>route</em> segment, and it is the claim's second predicate: a state
/// minted for one platform can never be completed through another platform's callback (F8). Until 07 adds
/// a second route the value is always <see cref="Platform.YouTube"/>, so the check costs nothing while it
/// is cheap to add; a mismatch is the same indistinguishable
/// <c>oauth_state_invalid</c> every other unclaimable state answers.
/// </para>
/// <para>
/// It is <see cref="NotAuditedAttribute"/> on purpose: pipeline slot 6 would write a row for every outcome,
/// including the four failures, while the design records <c>publishing.channel_connected</c> once and only
/// when a channel really changed (06.1.2-T1 task §Handlers step 10).
/// </para>
/// <para>
/// <b>Every failure is an <see cref="ErrorOr"/> error, and only the connected outcome is a value.</b> The
/// task file and the S58 contract show a bare enum, but every Mediator behaviour in this repo is
/// constrained to <c>TResponse : IErrorOr</c> (<c>MediatorPipeline</c>, and every command in Application
/// answers an <c>ErrorOr</c> — e.g. <c>RefreshCommand : ICommand&lt;ErrorOr&lt;RefreshResult&gt;&gt;</c>),
/// so a bare enum would get no pipeline at all: no <c>UnitOfWorkBehavior</c>, hence no transaction for
/// either boundary, and the claim's commit would throw (S58 slice B deviation, recorded in the task Notes).
/// The handler answers the four non-connected outcomes as errors carrying the catalogue's own codes
/// (<c>oauth_state_invalid</c>, <c>oauth_denied</c>, <c>oauth_exchange_failed</c>,
/// <c>channel_already_connected</c>), so <c>UnitOfWorkBehavior</c> <em>rolls back</em> the second unit of
/// work instead of committing it and <c>LoggingBehavior</c> records the failure as a failure. The endpoint
/// is the only place that turns a code back into one of the four members
/// (<see cref="ConnectYouTubeOutcome"/>) and then into one constant <c>302</c> target, so the enum is the
/// Api's redirect mapping and the handler never re-spells a wire value.
/// </para>
/// </remarks>
/// <param name="Platform">The platform of the route segment; the claim refuses a row minted for another one (F8).</param>
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
    NightTales.Domain.Publishing.Platform Platform,
    string? Code,
    string? State,
    string? Error,
    string? BrowserNonce,
    RequestInfo Info)
    : ICommand<ErrorOr<ConnectYouTubeOutcome>>
{
    /// <summary>
    /// Prints the route platform and nothing else (R8/F4): the compiler-generated list would put the
    /// authorization code, the state and the plaintext browser nonce into every log line, exception message
    /// and debugger string (06 README D3, YR10). The platform is already public — it is in the route and in
    /// the audit row — so it stays, and the repo's precedent
    /// (<c>FindSourcesCommand.PrintMembers</c>) is to keep only the safe members.
    /// </summary>
    /// <param name="builder">The record's string builder.</param>
    /// <returns><see langword="true"/>, because the platform is printed.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append(CultureInfo.InvariantCulture, $"Platform = {Platform}");
        return true;
    }
}

/// <summary>
/// What one callback did (06 README D3): the endpoint maps each member to one constant <c>302</c> target
/// and nothing else reaches the <c>Location</c> header. Only <see cref="Connected"/> is ever a handler
/// success value; the other four members are produced by the endpoint's code→outcome mapping from the
/// errors the handler returned, which is why the members and the wire codes are named in one place.
/// </summary>
public enum ConnectYouTubeOutcome
{
    /// <summary>The channel is connected (or reconnected): <c>/channels?connected=youtube</c>.</summary>
    Connected,

    /// <summary>
    /// Unknown, spent, expired, foreign or other-platform state, a missing or foreign browser nonce, a dead
    /// session family and an unreadable verifier cipher all answer this one member, so the callback is no
    /// oracle (<c>oauth_state_invalid</c>).
    /// </summary>
    StateInvalid,

    /// <summary>The user refused consent at Google (<c>oauth_denied</c>); the state is already spent.</summary>
    Denied,

    /// <summary>
    /// The exchange, the scope check or the channel read failed, the pinned client is no longer resolvable,
    /// or the connect writer is missing or refused the write (<c>oauth_exchange_failed</c>). The code is
    /// never retried.
    /// </summary>
    ExchangeFailed,

    /// <summary>This account already has a live connection for that channel (<c>channel_already_connected</c>).</summary>
    ChannelAlreadyConnected,
}
