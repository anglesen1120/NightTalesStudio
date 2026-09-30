namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The MFA challenge port of the login flow (09 README BD4), <em>declared</em> here by 09.3.2-T1 because
/// <c>POST /auth/login</c> is its first caller, while its implementation and its table
/// (<c>auth_challenges</c>) belong to 09.3.2-T3.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately empty and deliberately without an implementation: [RULED — README §B10 WT22, WT39] there
/// is no interim <c>PendingMfaChallengeService</c>. This task answers 401 <c>mfa_required</c> (TOTP
/// enrolled) or 401 <c>mfa_enrolment_required</c> (not enrolled) for an owner or admin and issues no
/// challenge document; 09.3.2-T3 edits the same login flow to call this port, adds the pre-auth cookie
/// and the <c>/auth/login/mfa</c> route.
/// </para>
/// <para>
/// Declaring the port now is what keeps that edit additive: the login flow's seam already exists, so T3
/// adds a method and an implementation instead of reshaping the handler.
/// </para>
/// </remarks>
public interface IMfaChallengeService
{
}
