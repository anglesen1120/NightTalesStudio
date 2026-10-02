using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Infrastructure.Email;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Email;

/// <summary>
/// The start-up gate of the <c>Email</c> section (09 README BD9/BD16, 09.2.6-T3): a deployment that would
/// hand a mail server a reset link in the clear, or that has no address pepper, must not start.
/// </summary>
/// <remarks>
/// <para>
/// The host is the smallest one that contains the rules — the <c>AddEmail</c> composition with a
/// hand-written configuration and no database — so the assertion is about the validation, not about
/// another host's wiring.
/// </para>
/// <para>
/// The development sink is loopback, and plaintext to loopback is the one transport the committed
/// configuration may use in any environment: the build-time OpenAPI generator starts the Api in Production
/// with exactly this configuration. Plaintext to a remote server is refused, which is what
/// <see cref="NonDevelopmentWithoutTls_FailsStartup"/> proves.
/// </para>
/// </remarks>
public sealed class EmailOptionsTests
{
    [Fact]
    public void NonDevelopmentWithoutTls_FailsStartup()
    {
        var exception = Should.Throw<OptionsValidationException>(() => Start("smtp.nighttales.test", "None"));

        exception.Message.ShouldBe(EmailOptionsValidator.InsecureTransportFailure);
    }

    [Fact]
    public void NonDevelopmentOnLoopbackWithoutTls_Starts()
    {
        Should.NotThrow(() => Start("127.0.0.1", "None"));

        Should.NotThrow(() => Start("localhost", "None"));
    }

    [Fact]
    public void NonDevelopmentWithTls_Starts()
    {
        Should.NotThrow(() => Start("smtp.nighttales.test", "StartTls"));

        Should.NotThrow(() => Start("smtp.nighttales.test", "StartTlsWhenAvailable"));

        Should.NotThrow(() => Start("smtp.nighttales.test", "SslOnConnect"));
    }

    [Fact]
    public void MissingPepper_FailsStartup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => Start("smtp.nighttales.test", "StartTls", pepper: string.Empty));

        exception.Message.ShouldBe(EmailOptionsValidator.MissingPepperFailure);
    }

    [Fact]
    public void MissingFromAddress_FailsStartup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => Start("smtp.nighttales.test", "StartTls", fromAddress: string.Empty));

        exception.Message.ShouldBe(EmailOptionsValidator.MissingFromAddressFailure);
    }

    [Fact]
    public void IncompleteCredentials_FailStartup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => Start("smtp.nighttales.test", "StartTls", username: "nts"));

        exception.Message.ShouldBe(EmailOptionsValidator.MissingCredentialsFailure);
    }

    [Fact]
    public void NonDevelopmentWithCommittedDevelopmentPepper_FailsStartup()
    {
        // A deployment that forgot its own pepper must not silently hash with the committed placeholder
        // (09 README BD16).
        var exception = Should.Throw<OptionsValidationException>(
            () => Start("smtp.nighttales.test", "StartTls", pepper: EmailOptions.DevelopmentAddressHashPepper));

        exception.Message.ShouldBe(EmailOptionsValidator.DevelopmentPepperFailure);
    }

    [Fact]
    public void NonDevelopmentOnLoopbackWithCommittedDevelopmentPepper_Starts()
    {
        // The committed loopback configuration is what the build-time OpenAPI generator boots in Production,
        // so the development pepper is refused only against a remote server.
        Should.NotThrow(() => Start("127.0.0.1", "None", pepper: EmailOptions.DevelopmentAddressHashPepper));
    }

    /// <summary>Builds the e-mail composition for one configuration and starts it.</summary>
    /// <param name="host">The SMTP host.</param>
    /// <param name="security">The configured transport security.</param>
    /// <param name="pepper">The address pepper; blank is the missing case.</param>
    /// <param name="fromAddress">The sender address; blank is the missing case.</param>
    /// <param name="username">The SMTP user, with no password next to it.</param>
    private static void Start(
        string host,
        string security,
        string pepper = "a-test-address-pepper",
        string fromAddress = "no-reply@nighttales.test",
        string? username = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Email:From:Address"] = fromAddress,
            ["Email:Smtp:Host"] = host,
            ["Email:Smtp:Security"] = security,
            ["Email:AddressHashPepper"] = pepper,
            ["App:PublicOrigin"] = "https://app.nighttales.test",
        };

        if (username is not null)
        {
            settings["Email:Smtp:Username"] = username;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddEmail(builder.Configuration);

        using var app = builder.Build();
        app.Start();
    }
}
