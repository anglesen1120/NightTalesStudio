using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The Data Protection key ring configuration (09 README BD12/WF15, 09.9.4-T1): the ring's application
/// name and the <c>DP_KEK</c> certificate that encrypts it before it reaches PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <c>DataProtection:KekPfxPath</c> and <c>DataProtection:KekPfxPassword</c> are the two halves of one
/// secret (<c>nts/&lt;env&gt;/shared/DP_KEK</c>, 11 §10:284): the Api and the Worker are given the same
/// PFX, so a value protected by one is unprotected by the other, and a mismatch is a visible
/// <c>TryUnprotect == false</c> rather than a silent re-key. Both are read from the secret store and
/// never committed; the password is never echoed in a failure message.
/// </para>
/// <para>
/// <c>DataProtection:ApplicationName</c> is the ring's isolation root. When nothing is configured it
/// resolves to <c>nts-{environment}</c> (BD12's documented default), read from
/// <see cref="IHostEnvironment"/> so the Api and the Worker of one environment agree by construction.
/// </para>
/// </remarks>
public sealed class DataProtectionKeyRingOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "DataProtection";

    /// <summary>The prefix of every environment-derived application name (<c>nts-{environment}</c>).</summary>
    public const string ApplicationNamePrefix = "nts-";

    /// <summary>The ring's application name; <c>nts-{environment}</c> when left unset.</summary>
    public string? ApplicationName { get; set; }

    /// <summary>The PFX file of the <c>DP_KEK</c> certificate; required at start-up.</summary>
    public string? KekPfxPath { get; set; }

    /// <summary>The PFX password; may be null for a password-less PFX.</summary>
    public string? KekPfxPassword { get; set; }

    /// <summary>Reads the section from <paramref name="configuration"/>.</summary>
    /// <param name="configuration">The host configuration.</param>
    public static DataProtectionKeyRingOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new DataProtectionKeyRingOptions
        {
            ApplicationName = NullIfBlank(
                configuration.GetValue<string?>($"{SectionName}:{nameof(ApplicationName)}")),
            KekPfxPath = NullIfBlank(
                configuration.GetValue<string?>($"{SectionName}:{nameof(KekPfxPath)}")),
            KekPfxPassword = configuration.GetValue<string?>($"{SectionName}:{nameof(KekPfxPassword)}"),
        };
    }

    /// <summary>The application name this host protects under: the configured one, or <c>nts-{environment}</c>.</summary>
    /// <param name="environment">The host's environment.</param>
    public string ResolveApplicationName(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return ApplicationName ?? $"{ApplicationNamePrefix}{environment.EnvironmentName}";
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
