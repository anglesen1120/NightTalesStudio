using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.Features.Auth.Register.Commands;

/// <summary>
/// The frozen <c>POST /api/v1/auth/register</c> body (10-web-ui SC30): exactly one <c>tos</c> and one
/// <c>privacy</c> acceptance, an optional invite code when the mode is <c>invite</c> and an optional
/// UI locale so the verification e-mail matches the client.
/// </summary>
/// <remarks>
/// <para>
/// Declared by 09.3.1-T1 as the request 09.3.1-T3 (register/verify) and 09.3.1-T2 (hardening) reuse and
/// extend (09.3.1-T2 adds <c>CaptchaToken</c>, W3 CR-9). The <c>age_18</c> attestation of 13.2.3-T1
/// (13 CR-11) arrives as a third <see cref="ConsentAcceptance"/>, not as a new command.
/// </para>
/// <para>
/// The namespace carries the <c>Commands</c> segment the architecture rules require
/// (<c>NightTales.Application.Features.*.(Commands|Queries).*</c>, 09.1.2-T1) and the folder holds
/// exactly one request, one handler and one validator.
/// </para>
/// <para>
/// <see cref="AllowAnonymousRequestAttribute"/> is the D4 marker: register is an anonymous route, and
/// without it the default-deny <c>AuthorizationBehavior</c> would answer 401 before the handler runs.
/// </para>
/// </remarks>
/// <param name="Email">The requested e-mail address (≤ 254 characters).</param>
/// <param name="Password">The requested password; never logged.</param>
/// <param name="Consents">The accepted document versions; exactly one <c>tos</c> and one <c>privacy</c>.</param>
/// <param name="InviteCode">The invite code, required only in mode <c>invite</c> (09.3.1-T3).</param>
/// <param name="UiLocale">The client's UI locale (<c>vi</c>/<c>en</c>), or null for the default.</param>
[AllowAnonymousRequest]
public sealed record RegisterCommand(
    string Email,
    string Password,
    IReadOnlyList<ConsentAcceptance> Consents,
    string? InviteCode = null,
    string? UiLocale = null) : ICommand<ErrorOr<Success>>;

/// <summary>One accepted legal-document version carried by <see cref="RegisterCommand"/>.</summary>
/// <param name="Document">
/// Which document was accepted. Required on the wire: without it JSON would bind the enum's default
/// (<see cref="LegalDocument.Tos"/>) and read as an implicit ToS acceptance (QA C3), so a body that
/// omits the member is refused as a malformed body (400 <c>validation_failed</c>).
/// </param>
/// <param name="Version">The accepted version as presented (non-empty, ≤ 32 characters).</param>
public sealed record ConsentAcceptance(
    [property: JsonRequired] LegalDocument Document,
    string Version);
