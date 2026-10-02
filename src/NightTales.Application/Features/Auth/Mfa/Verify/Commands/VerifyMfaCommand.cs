using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;

namespace NightTales.Application.Features.Auth.Mfa.Verify.Commands;

/// <summary>
/// <c>POST /api/v1/me/mfa/verify</c> (09.10.4-T1 Design): confirm the enrolment with a code from the
/// pending secret and receive the ten recovery codes.
/// </summary>
/// <remarks>
/// The code is verified against the <em>pending</em> secret ([RULED — README §B10 WT31]), so a code
/// computed from a secret that was never confirmed cannot be replayed here as a session credential. Like
/// setup, the route is open to the enrolment-only token or to a recent re-authentication (WT41).
/// </remarks>
/// <param name="Code">The six-digit code the authenticator application shows; never logged.</param>
[NotAudited("VerifyMfaHandler's flow writes the mfa.enrolled row itself through IAuditWriter, with the caller's IP and User-Agent (09.10.4-T1).")]
public sealed record VerifyMfaCommand(string Code) : ICommand<ErrorOr<IReadOnlyList<string>>>;
