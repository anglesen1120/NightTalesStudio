using System.Text.Encodings.Web;
using ErrorOr;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Realtime;

/// <summary>
/// The <c>HubTicket</c> scheme (09.8.1 plan §5): it authenticates the WebSocket handshake with the
/// single-use 60-second ticket of 09 README BD11 and nothing else — the access token never travels in a
/// URL (09-backend-api L214/R11).
/// </summary>
/// <remarks>
/// <para>
/// The ticket is read from the <c>ticket</c> query parameter and consumed through
/// <see cref="IHubTicketValidator"/>; a missing, unknown, expired, used or tampered-with ticket fails
/// the handshake with 401 <c>hub_ticket_invalid</c>, and a user who claims their own ticket gets a
/// principal with their <c>sub</c> and role — the group the hub can then join them to.
/// </para>
/// <para>
/// 09.8.1-T2 delivers the production validator together with the <c>hub_tickets</c> table. Until it
/// lands, no implementation is registered and a connect attempt fails fast with
/// <see cref="MissingValidatorMessage"/> — never an anonymous socket and never a permissive fallback.
/// </para>
/// <para>
/// The ticket is a bearer secret: it is never logged, never put in a metric label and never added to an
/// exception message here (09 README D17/BD16). A validator that fails is reported by its error type
/// only, which is what an operator needs to tell an outage from a wrong ticket.
/// </para>
/// </remarks>
public sealed partial class HubTicketAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider services)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The scheme name the hub's <c>[Authorize]</c> and its mapping use.</summary>
    public const string SchemeName = "HubTicket";

    // The [LoggerMessage] generator needs an ILogger-typed member on the class; the base handler only
    // keeps an ILoggerFactory, so derive a typed logger from it.
    private readonly ILogger<HubTicketAuthenticationHandler> _logger =
        logger.CreateLogger<HubTicketAuthenticationHandler>();

    /// <summary>The query parameter the client connects with (BD11: <c>/hubs/episodes?ticket=…</c>).</summary>
    public const string TicketParameterName = "ticket";

    /// <summary>
    /// The fail-fast message of the 09.8.1-T2 seam: no validator registration means no hub at all, and
    /// the message names the task that adds one.
    /// </summary>
    public const string MissingValidatorMessage = "IHubTicketValidator is not registered — delivered by 09.8.1-T2";

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ticket = Request.Query[TicketParameterName].ToString();
        if (string.IsNullOrWhiteSpace(ticket))
        {
            // No ticket: not an authentication attempt. The endpoint's [Authorize] turns this into the
            // challenge below, so the handshake ends in 401 hub_ticket_invalid, never in an open socket.
            return AuthenticateResult.NoResult();
        }

        var validator = services.GetService<IHubTicketValidator>()
            ?? throw new InvalidOperationException(MissingValidatorMessage);

        ErrorOr<HubTicketClaim> claim;
        try
        {
            claim = await validator.ClaimAsync(ticket, Context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A validator whose store is unreachable authenticates nobody: the handshake is refused and
            // the failure is recorded by type, never with the ticket or the store's message.
            LogClaimFailed(exception.GetType().Name);

            return AuthenticateResult.Fail(ErrorCodes.HubTicketInvalid);
        }

        if (claim.IsError)
        {
            LogTicketRejected();

            return AuthenticateResult.Fail(ErrorCodes.HubTicketInvalid);
        }

        return AuthenticateResult.Success(
            new AuthenticationTicket(HubIdentity.CreatePrincipal(claim.Value), SchemeName));
    }

    /// <summary>
    /// The 401 every failed ticket answers with: the host's own ProblemDetails shape, so the client sees
    /// the documented <c>hub_ticket_invalid</c> code and a trace id, and the ticket itself stays out of
    /// the response and the log.
    /// </summary>
    /// <param name="properties">The properties of the failed authentication; unused here, and may be null.</param>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var problem = ProblemDetailsWriter.ToProblem(
            [Error.Unauthorized(ErrorCodes.HubTicketInvalid, "The hub ticket is missing, expired, used or invalid.")],
            Context);

        await problem.ExecuteAsync(Context).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Warning,
        Message = "hub.ticket.rejected: a connect attempt presented no claimable ticket")]
    private partial void LogTicketRejected();

    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Error,
        Message = "hub.ticket.claim-failed: error={ErrorType}")]
    private partial void LogClaimFailed(string errorType);
}
