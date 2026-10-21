using NightTales.Application.Abstractions.Identity;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// <c>Registration</c> configuration (09 README BD2, D15): the mode the register gate answers with.
/// </summary>
/// <remarks>
/// <para>
/// P binds the mode from configuration and ships <c>closed</c> (W2-O1/W17; the acceptance check
/// "with <c>Registration:Mode = open</c> the same request still returns 403 in P" exists because the
/// <em>flow</em>, not this value, decides in P).
/// </para>
/// <para>
/// 09.3.1-T3 reads this through <c>IRegistrationPolicyProvider</c> and adds the invite/disposable-domain
/// keys; 09.10.6 replaces the provider's source with the owner-editable <c>registration.mode</c> setting
/// (09.3.1-T1's Notes carry the open item).
/// </para>
/// </remarks>
public sealed class RegistrationOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Registration";

    /// <summary>How sign-ups are answered; <see cref="RegistrationMode.Closed"/> in P.</summary>
    public RegistrationMode Mode { get; set; } = RegistrationMode.Closed;
}
