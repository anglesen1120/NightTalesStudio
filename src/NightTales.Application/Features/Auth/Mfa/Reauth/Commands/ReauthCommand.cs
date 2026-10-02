using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Mfa.Reauth.Commands;

/// <summary>
/// <c>POST /api/v1/auth/reauth</c> (09.10.4-T1 Design, BD4): prove the password (and the authenticator
/// code, when enrolled) again, so the sensitive routes can require a fresh proof.
/// </summary>
/// <remarks>
/// <para>
/// The result is the success-typed <see cref="ReauthResult"/>: a failure must commit its counter, its
/// possible family revocation and its audit row before the endpoint answers 401 ([RULED — README §B10
/// WT29]).
/// </para>
/// <para>
/// The route is authenticated (there is no <c>AllowAnonymousRequest</c> here): the caller's <c>sid</c> is
/// what the re-authentication stamps, so a caller without a session has nothing to re-authenticate.
/// </para>
/// </remarks>
/// <param name="Password">The submitted password; never logged.</param>
/// <param name="Code">
/// The submitted six-digit authenticator code, or null when the account has no authenticator enrolled.
/// </param>
[NotAudited("ReauthHandler's flow writes the account.reauth_failed row itself through IAuditWriter, with the caller's IP and User-Agent (09.10.4-T1).")]
public sealed record ReauthCommand(string Password, string? Code) : ICommand<ErrorOr<ReauthResult>>;
