using System.Security.Claims;
using NightTales.Api.Security;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Common;

namespace NightTales.Api.Realtime;

/// <summary>
/// The identity of a hub connection: the platform claims of 09 README D16 (<c>sub</c>, <c>role</c>) as
/// the <c>HubTicket</c> scheme writes them and the hub reads them.
/// </summary>
/// <remarks>
/// <para>
/// The hub deliberately does not use <c>HubCallerContext.UserIdentifier</c>: SignalR computes it with
/// its default <c>IUserIdProvider</c>, which reads <c>ClaimTypes.NameIdentifier</c> — a claim our
/// ES256 tokens never carry, because <c>MapInboundClaims</c> is off and D16 keeps the raw <c>sub</c>
/// (09.3.2-T1). Reading the platform's own claim here is the one source that always holds the user,
/// and the group of a connection is therefore never a value a client can influence.
/// </para>
/// <para>
/// The role is read from the principal — the claim the ticket's claim produced — never from a cache or
/// a database: [RULED — README §B10 WT37] keeps the hub path free of any stamp or status lookup.
/// </para>
/// </remarks>
public static class HubIdentity
{
    /// <summary>
    /// The principal a claimed ticket authenticates as: the user's id as <c>sub</c> and the user's
    /// platform role as <c>role</c>, exactly the claims <see cref="HttpCurrentUser"/> reads for a
    /// bearer request.
    /// </summary>
    /// <param name="claim">The claimed ticket's user and role.</param>
    public static ClaimsPrincipal CreatePrincipal(HubTicketClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim(HttpCurrentUser.SubjectClaimType, claim.UserId.ToString("D")),
                    new Claim(HttpCurrentUser.RoleClaimType, RoleClaimValue(claim.Role)),
                ],
                HubTicketAuthenticationHandler.SchemeName));
    }

    /// <summary>The <c>sub</c> claim as a user id, or <c>false</c> when it is absent, unparsable or empty.</summary>
    /// <param name="principal">The connection's principal.</param>
    /// <param name="userId">The user id when the claim is usable.</param>
    public static bool TryGetUserId(ClaimsPrincipal? principal, out Guid userId)
    {
        var raw = principal?.FindFirst(HttpCurrentUser.SubjectClaimType)?.Value;

        return Guid.TryParse(raw, out userId) && userId != Guid.Empty;
    }

    /// <summary>
    /// The connection's role, from the closed set of <see cref="PlatformRole"/> names and believed only
    /// behind a usable <c>sub</c> (09 README D10): without a user the connection is
    /// <see cref="PlatformRole.User"/>, never an elevated role.
    /// </summary>
    /// <param name="principal">The connection's principal.</param>
    public static PlatformRole RoleOf(ClaimsPrincipal? principal)
    {
        if (!TryGetUserId(principal, out _))
        {
            return PlatformRole.User;
        }

        var raw = principal!.FindFirst(HttpCurrentUser.RoleClaimType)?.Value;

        foreach (var role in Enum.GetValues<PlatformRole>())
        {
            if (string.Equals(role.ToString(), raw, StringComparison.OrdinalIgnoreCase))
            {
                return role;
            }
        }

        return PlatformRole.User;
    }

    /// <summary>The snake_case <c>role</c> claim value of the wire contract (09 README D16).</summary>
    /// <param name="role">The platform role.</param>
    public static string RoleClaimValue(PlatformRole role) => role.ToString().ToLowerInvariant();
}
