using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;

namespace NightTales.Application.Features.Auth.Mfa.Disable.Commands;

/// <summary>
/// <c>DELETE /api/v1/me/mfa</c> (09.10.4-T1 Design): disable MFA for the caller.
/// </summary>
/// <remarks>
/// <para>
/// An owner or an admin can never disable MFA (BD4): the handler answers 403 <c>forbidden</c> before the
/// flow runs, because for those two roles MFA is not an option to turn off but a condition of holding the
/// role.
/// </para>
/// <para>
/// Any other authenticated caller needs a recent re-authentication (<c>RequireReauth</c>); the flow then
/// deletes the secrets and codes, turns <c>two_factor_enabled</c> off, rotates the stamp and revokes every
/// family but the caller's own.
/// </para>
/// </remarks>
[NotAudited("DisableMfaHandler's flow writes the mfa.disabled row itself through IAuditWriter, with the caller's IP and User-Agent (09.10.4-T1).")]
public sealed record DisableMfaCommand : ICommand<ErrorOr<Success>>;
