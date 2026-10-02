using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// The start-up rules of the <c>Email</c> section (09 README BD9/BD16, 09.2.6-T3): the sender address must
/// be a usable mailbox, the endpoint must be dialable, a plaintext connection and the development pepper
/// must not survive into a deployment, and credentials must be complete or absent.
/// </summary>
/// <remarks>
/// Applied with <c>ValidateOnStart</c> by <c>AddEmail</c>, so a host that cannot send a correct message —
/// or that would send one in the clear — refuses to start instead of failing inside a job hours later. The
/// messages name the configuration key and the rule and never echo a value, so a failure is safe to paste
/// into an issue.
/// </remarks>
public static class EmailOptionsValidator
{
    /// <summary>The failure of an absent <c>Email:From:Address</c>.</summary>
    public const string MissingFromAddressFailure =
        "'Email:From:Address' is required: every message needs a sender mailbox (09 README BD9, 09.2.6-T3).";

    /// <summary>The failure of a sender address that is not a mailbox.</summary>
    public const string UnusableFromAddressFailure =
        "'Email:From:Address' must be a usable mailbox address, for example no-reply@nighttales.app (09.2.6-T3).";

    /// <summary>The failure of an absent <c>Email:Smtp:Host</c>.</summary>
    public const string MissingHostFailure =
        "'Email:Smtp:Host' is required; the development default is 127.0.0.1 (11.1.1-T2's Mailpit sink).";

    /// <summary>The failure of a port outside the TCP range.</summary>
    public const string InvalidPortFailure =
        "'Email:Smtp:Port' must be a TCP port between 1 and 65535.";

    /// <summary>The failure of a timeout outside the accepted range.</summary>
    public const string InvalidTimeoutFailure =
        "'Email:Smtp:TimeoutSeconds' must be between 1 and 600.";

    /// <summary>
    /// The failure of <c>Security = None</c> outside Development against a remote server. The development
    /// sink of 11.1.1-T2 is loopback, where plaintext never leaves the machine, and that — not the
    /// environment name — is what the rule turns on, because the committed configuration has to boot in
    /// every environment (the build-time OpenAPI generator starts the Api in Production).
    /// </summary>
    public const string InsecureTransportFailure =
        "'Email:Smtp:Security' must not be 'None' outside Development unless 'Email:Smtp:Host' is loopback "
        + "(127.0.0.1, ::1 or localhost): reset links and verification tokens are never handed to a remote "
        + "mail server over a plaintext connection (09 README BD9).";

    /// <summary>The failure of an incomplete credential pair.</summary>
    public const string MissingCredentialsFailure =
        "'Email:Smtp:Username' and 'Email:Smtp:Password' must be set together, or both left unset for a "
        + "server that accepts anonymous mail such as the development sink (09.2.6-T3).";

    /// <summary>The failure of an absent address pepper.</summary>
    public const string MissingPepperFailure =
        "'Email:AddressHashPepper' is required: without it the address hash of `email_messages` could be "
        + "brute-forced (09 README BD16).";

    /// <summary>
    /// The failure of the committed development pepper outside Development. Unlike the transport rule this
    /// one turns on the environment, not the endpoint: the pepper is the salt that keeps a small address
    /// space from being brute-forced out of <c>email_messages.to_hash</c> (09 README BD16), so it may be the
    /// committed placeholder only in Development. Every other environment supplies its own value, exactly
    /// the way it supplies the signing key and the <c>DP_KEK</c> — including the build-time OpenAPI
    /// generator, which boots the Api in Production and is handed one through the environment.
    /// </summary>
    public const string DevelopmentPepperFailure =
        "'Email:AddressHashPepper' must not be the committed development placeholder outside Development: "
        + "set the deployment's own pepper (Email__AddressHashPepper) so `email_messages.to_hash` cannot be "
        + "brute-forced (09 README BD16).";

    /// <summary>Adds every rule to <paramref name="builder"/> and turns validation on at start-up.</summary>
    /// <param name="builder">The options builder of <c>AddEmail</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    internal static OptionsBuilder<EmailOptions> Apply(OptionsBuilder<EmailOptions> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.From.Address),
                MissingFromAddressFailure)
            .Validate(
                options => string.IsNullOrWhiteSpace(options.From.Address)
                    || MailboxAddress.TryParse(options.From.Address, out _),
                UnusableFromAddressFailure)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Smtp.Host),
                MissingHostFailure)
            .Validate(
                options => options.Smtp.Port is > 0 and <= 65535,
                InvalidPortFailure)
            .Validate(
                options => options.Smtp.TimeoutSeconds is > 0 and <= EmailOptions.MaxSendTimeoutSeconds,
                InvalidTimeoutFailure)
            .Validate<IHostEnvironment>(
                (options, environment) => environment.IsDevelopment()
                    || options.Smtp.Security != SmtpSecurity.None
                    || IsLoopbackHost(options.Smtp.Host),
                InsecureTransportFailure)
            .Validate(
                options => string.IsNullOrWhiteSpace(options.Smtp.Username) == string.IsNullOrWhiteSpace(options.Smtp.Password),
                MissingCredentialsFailure)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.AddressHashPepper),
                MissingPepperFailure)
            .Validate<IHostEnvironment>(
                (options, environment) => environment.IsDevelopment()
                    || !string.Equals(
                        options.AddressHashPepper,
                        EmailOptions.DevelopmentAddressHashPepper,
                        StringComparison.Ordinal),
                DevelopmentPepperFailure)
            .ValidateOnStart();
    }

    /// <summary>
    /// Whether <paramref name="host"/> is a loopback endpoint: the literal <c>localhost</c> or an IP the
    /// platform itself classifies as loopback (<c>127.0.0.0/8</c>, <c>::1</c>).
    /// </summary>
    /// <param name="host">The configured <c>Email:Smtp:Host</c>.</param>
    internal static bool IsLoopbackHost(string? host)
    {
        var value = host?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(value, out var address) && System.Net.IPAddress.IsLoopback(address);
    }
}
