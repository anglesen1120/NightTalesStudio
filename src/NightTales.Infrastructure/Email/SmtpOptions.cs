namespace NightTales.Infrastructure.Email;

/// <summary>
/// The <c>Email:Smtp</c> endpoint (09.2.6-T3): where the Worker's <c>SendEmailJob</c> connects, with which
/// transport security and with which credentials.
/// </summary>
public sealed class SmtpOptions
{
    /// <summary>The development default: 11.1.1-T2's Mailpit sink on loopback.</summary>
    public const string DevelopmentHost = "127.0.0.1";

    /// <summary>The development default: Mailpit's SMTP port.</summary>
    public const int DevelopmentPort = 1025;

    /// <summary>The default connection timeout in seconds.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>The host, required; the development default is the loopback sink.</summary>
    public string Host { get; set; } = DevelopmentHost;

    /// <summary>The TCP port; the development default is Mailpit's.</summary>
    public int Port { get; set; } = DevelopmentPort;

    /// <summary>The transport security; never <see cref="SmtpSecurity.None"/> outside Development.</summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.None;

    /// <summary>The SMTP user, or <see langword="null"/> for a server that accepts anonymous mail.</summary>
    public string? Username { get; set; }

    /// <summary>The SMTP password (from the deployment's secret store), or <see langword="null"/>.</summary>
    public string? Password { get; set; }

    /// <summary>The per-connection timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
}

/// <summary>
/// How the SMTP connection is protected (09 README BD9: TLS is required, the development sink is the one
/// exception).
/// </summary>
public enum SmtpSecurity
{
    /// <summary>Plaintext. Development only; the validator refuses it anywhere else.</summary>
    None,

    /// <summary>Refuse to continue when the server does not offer <c>STARTTLS</c>.</summary>
    StartTls,

    /// <summary>Use <c>STARTTLS</c> when the server offers it, otherwise continue unencrypted.</summary>
    StartTlsWhenAvailable,

    /// <summary>Implicit TLS from the first byte (port 465).</summary>
    SslOnConnect,
}
