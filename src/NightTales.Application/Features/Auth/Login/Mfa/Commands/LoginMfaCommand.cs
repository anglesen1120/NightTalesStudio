using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Login.Mfa.Commands;

/// <summary>
/// <c>POST /api/v1/auth/login/mfa</c> (09.3.2-T3 Design, BD4): exchange the challenge of the password
/// step plus a TOTP or recovery code for the session's tokens.
/// </summary>
/// <remarks>
/// <para>
/// The result is the success-typed <see cref="MfaChallengeOutcome"/>: a wrong code and a dead challenge
/// both leave committed state behind (the attempt counter, the possible challenge death, the audit
/// rows), so the handler answers with the outcome and the endpoint maps it to 401/429
/// ([RULED — README §B10 WT29]).
/// </para>
/// <para>
/// <see cref="PreAuthCookie"/> is not part of the JSON body: the endpoint reads the
/// <c>__Secure-nts_mfa</c> cookie and builds the command, exactly as <c>/auth/refresh</c> reads its own
/// cookie. Letting a client submit the value would make the second factor guessable from the body
/// alone.
/// </para>
/// </remarks>
/// <param name="MfaToken">The single-use token of the login step's 401 body; never logged.</param>
/// <param name="Code">The six-digit authenticator code, or null when a recovery code is submitted.</param>
/// <param name="RecoveryCode">The recovery code, or null when an authenticator code is submitted.</param>
/// <param name="PreAuthCookie">The plaintext pre-auth cookie the Api read; never logged.</param>
[NotAudited("LoginMfaHandler's flow writes the account.login_succeeded/account.mfa_failed rows itself through IAuditWriter, with the caller's IP and User-Agent (09.3.2-T3).")]
[AllowAnonymousRequest]
public sealed record LoginMfaCommand(
    string MfaToken,
    string? Code,
    string? RecoveryCode,
    string PreAuthCookie) : ICommand<ErrorOr<MfaChallengeOutcome>>;
