using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Mfa.Setup.Commands;

/// <summary>
/// <c>POST /api/v1/me/mfa/setup</c> (09.10.4-T1 Design): start (or restart) the authenticator enrolment.
/// </summary>
/// <remarks>
/// <para>
/// The command has no body: the account is the caller's, resolved from <c>ICurrentUser</c>, so there is
/// nothing a client could submit — and nothing it could submit about somebody else.
/// </para>
/// <para>
/// The route is open to two kinds of caller ([RULED — README §B10 WT41]): an owner or admin holding the
/// enrolment-only token (BD4), and any authenticated caller with a recent re-authentication — the
/// <c>RequireReauthUnlessEnrolment</c> filter of the endpoint is what enforces that, not this handler.
/// </para>
/// </remarks>
[NotAudited("SetupMfaHandler's flow writes the mfa.setup_started row itself through IAuditWriter, with the caller's IP and User-Agent (09.10.4-T1).")]
public sealed record SetupMfaCommand : ICommand<ErrorOr<MfaSetup>>;
