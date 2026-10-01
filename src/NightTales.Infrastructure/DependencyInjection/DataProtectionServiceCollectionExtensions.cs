using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Security;

// The framework's `DataProtectionOptions` carries the ring's application discriminator; this task's
// `DataProtectionKeyRingOptions` carries the configuration of BD12/WF15. The alias keeps the two apart.
using FrameworkDataProtectionOptions = Microsoft.AspNetCore.DataProtection.DataProtectionOptions;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the platform's Data Protection key ring and the <see cref="ITokenProtector"/> over it
/// (09 README BD12/WF15, 09.9.4-T1).
/// </summary>
/// <remarks>
/// <para>
/// Both composition roots call this — the Api and the Worker — because both protect and unprotect values
/// with the same ring (11 §10:284 gives them the same <c>DP_KEK</c>): the Api signs a channel-connection
/// token, the Worker reads it back, and the TOTP secret store of 09.10.4-T1 uses the same ring under its
/// own purpose root. Registering it in the composition root and not in <c>AddPersistence</c> keeps the
/// design-time model, the migration tests and the model unit tests — none of which protect anything — free
/// of the required KEK.
/// </para>
/// <para>
/// The key ring lives in PostgreSQL (<c>data_protection_keys</c>, the migration of this task) and is
/// encrypted with the <c>DP_KEK</c> certificate before it is written: a database dump alone cannot
/// unprotect a refresh token or an action link. There is no <c>PersistKeysToFileSystem</c> anywhere, so a
/// container's ephemeral filesystem never holds the ring.
/// </para>
/// <para>
/// The KEK is loaded here, while the composition root runs, and the failure is fixed and immediate: an
/// absent, unreadable, private-key-less or non-RSA PFX stops the host rather than letting it protect
/// values under a key it cannot read back (WF15's failure table).
/// </para>
/// </remarks>
public static class DataProtectionServiceCollectionExtensions
{
    /// <summary>Registers the key ring, the <c>DP_KEK</c> and <see cref="ITokenProtector"/>.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration (<c>DataProtection:*</c>).</param>
    public static IServiceCollection AddNightTalesDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var ring = DataProtectionKeyRingOptions.From(configuration);

        // The framework keeps this very instance inside the ring's encryptor and decryptor, so it lives
        // for the process and is deliberately not disposed: the host's shutdown would only race the key
        // manager's own use of it.
        var kek = DataProtectionKek.Load(ring);

        services.AddDataProtection()
            // The ring is the row store of 09.2.1-T1's context; `IDataProtectionKeyContext` is what makes
            // this overload resolvable.
            .PersistKeysToDbContext<AppDbContext>()
            // WF15: the ring's XML is encrypted with the DP_KEK certificate before it is persisted, so the
            // `xml` column never holds readable key material.
            .ProtectKeysWithCertificate(kek.Certificate);

        // BD12's application name is resolved through the options pipeline rather than with
        // SetApplicationName, because its documented default (`nts-<environment>`) needs the host's
        // environment name and the composition root does not have it — only the container does.
        services.AddOptions<FrameworkDataProtectionOptions>()
            .PostConfigure<IHostEnvironment>((options, environment) =>
                options.ApplicationDiscriminator = ring.ResolveApplicationName(environment));

        services.AddSingleton<ITokenProtector, DataProtectionTokenProtector>();

        return services;
    }
}
