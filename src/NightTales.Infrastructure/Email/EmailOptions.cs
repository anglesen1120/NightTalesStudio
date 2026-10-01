using Microsoft.Extensions.Configuration;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// The <c>Email</c> section of the host configuration (09.2.6-T3): who the platform sends as, how it
/// reaches a mail server, and the pepper behind the stored address hash.
/// </summary>
/// <remarks>
/// <para>
/// Defaults are the daily development path (11.1.1-T2's Mailpit on <c>127.0.0.1:1025</c>), and the rules
/// that must never hold outside Development — a plaintext connection, the development pepper — are refused
/// by <see cref="EmailOptionsValidator"/> at start-up instead of being silently inherited by a deployment.
/// </para>
/// <para>
/// The secrets themselves (<c>Email:Smtp:Password</c>, <c>Email:AddressHashPepper</c>) come from the
/// deployment's secret store through the environment exactly as every other secret of the platform does
/// (<c>Email__Smtp__Password</c>, <c>Email__AddressHashPepper</c>); 09.2.6-T3 deliberately does not
/// invent a secret-ref resolver.
/// </para>
/// </remarks>
public sealed class EmailOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Email";

    /// <summary>
    /// The flat section the development environment uses for the same SMTP settings
    /// (<c>SMTP__HOST</c>, <c>SMTP__PORT</c>, … — 11.1.1-T2's <c>.env</c>). It is bound first, so an
    /// explicit <c>Email:Smtp:*</c> value always wins.
    /// </summary>
    public const string SmtpAliasSectionName = "Smtp";

    /// <summary>The longest accepted SMTP timeout, so a job cannot be pinned by a hung server.</summary>
    public const int MaxSendTimeoutSeconds = 600;

    /// <summary>The sender identity every message is composed with.</summary>
    public EmailFromOptions From { get; set; } = new();

    /// <summary>The SMTP endpoint and its credentials.</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>
    /// The secret appended to a normalized recipient address before hashing (09 README BD16): without it, a
    /// small address space could be brute-forced from <c>email_messages.to_hash</c>.
    /// </summary>
    /// <remarks>
    /// The committed <c>appsettings.json</c> carries a development placeholder so that a laptop, the
    /// build-time OpenAPI generator and the test harnesses all boot without a secret store; a deployment
    /// overrides it with its own value (<c>Email__AddressHashPepper</c>, 11 §10). Rotating it invalidates
    /// nothing — the hash is a correlation value, not a key — but rows hashed under the old pepper no longer
    /// correlate with the same address.
    /// </remarks>
    public string AddressHashPepper { get; set; } = string.Empty;

    /// <summary>
    /// Fills <paramref name="options"/> from <paramref name="configuration"/> with the section's precedence:
    /// the flat development alias first, then <c>Email:*</c> over it.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="options">The instance being bound.</param>
    internal static void Bind(IConfiguration configuration, EmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        configuration.GetSection(SectionName).Bind(options);

        // 11.1.1-T2's `.env`/`up.sh` hand the Api and the Worker `SMTP__HOST`/`SMTP__PORT` (and the
        // credentials) as flat environment keys, which .NET reads as the `Smtp` section. Binding them under
        // the canonical `Email:Smtp:*` values keeps one host working with either spelling.
        configuration.GetSection(SmtpAliasSectionName).Bind(options.Smtp);
        configuration.GetSection($"{SectionName}:{nameof(Smtp)}").Bind(options.Smtp);
    }
}

/// <summary>The <c>Email:From</c> identity (09 README BD9).</summary>
public sealed class EmailFromOptions
{
    /// <summary>The default display name every message is sent as.</summary>
    public const string DefaultName = "NightTales";

    /// <summary>The sender address; required, and validated as a usable mailbox at start-up.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The display name next to <see cref="Address"/>.</summary>
    public string Name { get; set; } = DefaultName;
}
