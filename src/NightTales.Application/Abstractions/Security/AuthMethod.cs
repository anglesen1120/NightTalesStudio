namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// How the caller proved who they are for the access token being issued (09 README BD3): the value of
/// the <c>amr</c> claim.
/// </summary>
/// <remarks>
/// The wire values are the <c>pwd</c>, <c>otp</c> and <c>rc</c> spellings BD3 freezes; they are not
/// the member names, so the mapping lives in <see cref="AuthMethodExtensions.ToAmrClaim"/> rather than
/// being re-spelled by every caller.
/// </remarks>
public enum AuthMethod
{
    /// <summary>A password login (09.3.2-T1), including the MFA step's own token (09.3.2-T3).</summary>
    Pwd,

    /// <summary>An authenticator code completed the login (09.3.2-T3, BD4).</summary>
    Otp,

    /// <summary>A recovery code completed the login (09.10.4-T1, BD4).</summary>
    Rc,
}

/// <summary>The <c>amr</c> wire spelling of <see cref="AuthMethod"/> (09 README BD3).</summary>
public static class AuthMethodExtensions
{
    /// <summary>The value the access token carries for <paramref name="method"/>.</summary>
    /// <param name="method">The method that produced the token.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="AuthMethod"/>.</exception>
    public static string ToAmrClaim(this AuthMethod method) => method switch
    {
        AuthMethod.Pwd => "pwd",
        AuthMethod.Otp => "otp",
        AuthMethod.Rc => "rc",
        _ => throw new ArgumentOutOfRangeException(
            nameof(method),
            method,
            "Only pwd, otp and rc are accepted values of the amr claim (09 README BD3)."),
    };
}
