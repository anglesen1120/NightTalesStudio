using System.Globalization;
using NightTales.Infrastructure.Email;

namespace NightTales.TestKit.Email;

/// <summary>
/// The e-mail configuration every test host boots with (09 README D13, 09.2.6-T3): the sender, the
/// loopback development sink of 11.1.1-T2, a pepper of the run's own and the origin links are composed
/// from.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline validates all of this on start-up, and both hosts register it, so the Worker harness and
/// the Api factory both need the values — the same shape a deployment supplies through its configuration
/// and secret store. The pepper is a plain string here on purpose: a test host must not need a secret
/// store, and the value protects nothing.
/// </para>
/// <para>
/// The keys are public so a test can override exactly what it cares about: <c>EmailEndToEndTests</c> only
/// moves the host and port to Mailpit's, and keeps the rest.
/// </para>
/// </remarks>
public static class EmailTestSettings
{
    /// <summary>The sender address every test host sends as.</summary>
    public const string FromAddress = "harness@nighttales.test";

    /// <summary>The address pepper of the test runs; never a deployment's value.</summary>
    public const string AddressHashPepper = "harness-address-pepper";

    /// <summary>The origin links of a test message are composed from.</summary>
    public const string PublicOrigin = "https://harness.nighttales.test";

    /// <summary>The configuration key of <c>Email:From:Address</c>.</summary>
    public const string FromAddressKey = $"{EmailOptions.SectionName}:{nameof(EmailOptions.From)}:{nameof(EmailFromOptions.Address)}";

    /// <summary>The configuration key of <c>Email:Smtp:Host</c>.</summary>
    public const string SmtpHostKey = $"{EmailOptions.SectionName}:{nameof(EmailOptions.Smtp)}:{nameof(SmtpOptions.Host)}";

    /// <summary>The configuration key of <c>Email:Smtp:Port</c>.</summary>
    public const string SmtpPortKey = $"{EmailOptions.SectionName}:{nameof(EmailOptions.Smtp)}:{nameof(SmtpOptions.Port)}";

    /// <summary>The configuration key of <c>Email:Smtp:Security</c>.</summary>
    public const string SmtpSecurityKey = $"{EmailOptions.SectionName}:{nameof(EmailOptions.Smtp)}:{nameof(SmtpOptions.Security)}";

    /// <summary>The configuration key of <c>Email:AddressHashPepper</c>.</summary>
    public const string AddressHashPepperKey = $"{EmailOptions.SectionName}:{nameof(EmailOptions.AddressHashPepper)}";

    /// <summary>The configuration key of <c>App:PublicOrigin</c>.</summary>
    public const string PublicOriginKey = "App:PublicOrigin";

    /// <summary>
    /// What a Worker or Api host needs to boot its e-mail pipeline: the loopback sink, plaintext to it (a
    /// loopback endpoint is the one place the transport rule allows), the run's pepper and origin.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> WorkerSettings { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [FromAddressKey] = FromAddress,
            [SmtpHostKey] = SmtpOptions.DevelopmentHost,
            [SmtpPortKey] = SmtpOptions.DevelopmentPort.ToString(CultureInfo.InvariantCulture),
            [SmtpSecurityKey] = nameof(SmtpSecurity.None),
            [AddressHashPepperKey] = AddressHashPepper,
            [PublicOriginKey] = PublicOrigin,
        };
}
