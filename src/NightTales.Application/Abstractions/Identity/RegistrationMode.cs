namespace NightTales.Application.Abstractions.Identity;

/// <summary>
/// How <c>POST /auth/register</c> answers, bound from <c>Registration:Mode</c> (09 README BD2, D15).
/// P ships <see cref="Closed"/> only (09 README W17/W2-O1); 09.3.1-T3 implements <see cref="Invite"/>
/// and <see cref="Open"/>.
/// </summary>
public enum RegistrationMode
{
    /// <summary>Sign-ups closed: every request answers 403 <c>registration_closed</c>.</summary>
    Closed,

    /// <summary>Sign-ups need the invite code of 09.3.1-T3.</summary>
    Invite,

    /// <summary>Public sign-up (09.3.1-T3, S).</summary>
    Open,
}
