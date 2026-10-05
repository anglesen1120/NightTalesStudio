using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Identity;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// Registers ASP.NET Core Identity for the platform (09.3.1-T1, 09 README BD1/BD2/D8) plus the
/// registration/password-policy/consent services of this task.
/// </summary>
/// <remarks>
/// <para>
/// Composition: <c>AddIdentityCore&lt;AppUser&gt;</c> with the BD2 options, roles, the EF stores over
/// <see cref="AppDbContext"/> and the default token providers. This is deliberately
/// <c>AddIdentityCore</c>, not <c>AddIdentity</c>/<c>AddDefaultIdentity</c>: the platform owns its
/// cookie/JWT scheme (09.3.2-T1), so no authentication scheme is registered here.
/// </para>
/// <para>
/// Called by <c>AddPersistence</c> — the one place every host (Api, Worker, the test harness) composes
/// infrastructure. 09.3.1-T1's design names that seam <c>AddInfrastructure</c>; the method does not
/// exist in this codebase, so the call sits in the existing composition root.
/// </para>
/// </remarks>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// The BD2 password length. Identity's own validators enforce it (inside
    /// <c>UserManager.CreateAsync</c>) and <c>IdentityPasswordPolicy</c> mirrors it as a pre-check.
    /// </summary>
    public const int RequiredPasswordLength = 12;

    /// <summary>Registers Identity, its options and the identity services of 09.3.1-T1.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration (registration, breached passwords, legal versions).</param>
    public static IServiceCollection AddIdentityServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequiredLength = RequiredPasswordLength;
                options.Password.RequiredUniqueChars = 1;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                // BD2: no account lockout. Brute force is 09.3.3's counters and CAPTCHA, not Identity's
                // lockout (which would be a denial-of-service lever against a known e-mail).
                options.Lockout.AllowedForNewUsers = false;

                // BD2: one account per e-mail; uniqueness rides the normalized_email index (D8, no citext).
                options.User.RequireUniqueEmail = true;

                // 09.3.2-T1 answers 403 email_not_verified after a successful password check, so Identity
                // must not demand a confirmed e-mail here.
                options.SignIn.RequireConfirmedEmail = false;

                // BD4: 09.10.4-T1 protects the TOTP secret itself (Data Protection purpose
                // identity.totp.v1), so Identity's personal-data protector stays out of the way.
                options.Stores.ProtectPersonalData = false;
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>();

        // `AddDefaultTokenProviders()` is deliberately NOT called: it lives in the
        // Microsoft.AspNetCore.Identity assembly, which this project does not reference (W1's N-list does
        // not name it), and every token this epic needs is its own — 09.3.3-T1's `user_action_tokens`,
        // 09.10.4-T1's TOTP/recovery codes, 09.3.1-T3's e-mail link. Identity's own email/reset token
        // providers would additionally drag DataProtection (09.9.4-T1) into the registration path.

        AddOptions(services, configuration);
        AddServices(services);

        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RegistrationOptions>()
            .BindConfiguration(RegistrationOptions.SectionName)
            .Validate(
                options => Enum.IsDefined(options.Mode),
                $"'{RegistrationOptions.SectionName}:{nameof(RegistrationOptions.Mode)}' must be one of closed, invite, open.")
            .ValidateOnStart();

        services.AddOptions<BreachedPasswordOptions>()
            .BindConfiguration(BreachedPasswordOptions.SectionName)
            .Validate(
                options => Enum.IsDefined(options.Mode),
                $"'{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.Mode)}' must be one of range, list, off.")
            .Validate(
                options => options.TopListSize > 0,
                $"'{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.TopListSize)}' must be greater than zero.")
            .Validate(
                options => options.RangeTimeoutSeconds > 0,
                $"'{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.RangeTimeoutSeconds)}' must be greater than zero.")
            .Validate(
                options => options.Mode is not BreachedPasswordMode.Range
                    || Uri.TryCreate(options.RangeBaseUrl, UriKind.Absolute, out _),
                $"'{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.RangeBaseUrl)}' must be an absolute URL in range mode.")
            .Validate(
                // Fail fast: a missing, empty or unusable list must stop the host, never silently accept
                // every password. The check stops at the first entry; the checker's constructor is the
                // one full load.
                options => options.Mode is not BreachedPasswordMode.List || TopListPasswordChecker.HasUsableBundledList(),
                $"Security:BreachedPasswords:Mode = list needs the bundled top-list resource '{TopListPasswordChecker.ResourceName}' "
                + "with at least one entry.")
            .ValidateOnStart();

        services.AddOptions<LegalDocumentVersions>()
            .BindConfiguration(LegalDocumentVersions.SectionName)
            .Validate(
                options => IsVersion(options.Tos),
                $"'{LegalDocumentVersions.SectionName}:{nameof(LegalDocumentVersions.Tos)}' is required, semver-like (1.0.0), and at most {LegalDocumentVersions.VersionMaxLength} characters.")
            .Validate(
                options => IsVersion(options.Privacy),
                $"'{LegalDocumentVersions.SectionName}:{nameof(LegalDocumentVersions.Privacy)}' is required, semver-like (1.0.0), and at most {LegalDocumentVersions.VersionMaxLength} characters.")
            .ValidateOnStart();
    }

    private static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<TopListPasswordChecker>();
        services.AddSingleton<IBreachedPasswordChecker>(CreateBreachedPasswordChecker);

        // Password policy is stateless over options + the checker above: one instance per host.
        services.AddSingleton<IPasswordPolicy, IdentityPasswordPolicy>();

        // Credentials and consents touch the scoped Identity store / write model.
        services.AddScoped<ICredentialVerifier, IdentityCredentialVerifier>();
        services.AddScoped<IConsentWriter, ConsentWriter>();
        services.AddScoped<IConsentReader, ConsentReader>();
        services.AddSingleton<ICurrentLegalVersions>(provider => provider.GetRequiredService<IOptions<LegalDocumentVersions>>().Value);
        services.AddScoped<IUserProfileReader, UserProfileReader>();
        services.AddScoped<IUserProfileWriter, UserProfileWriter>();

        // The P registration behaviour (09.3.1-T3 replaces exactly this registration).
        services.AddSingleton<IRegistrationFlow, ClosedRegistrationFlow>();
    }

    private static IBreachedPasswordChecker CreateBreachedPasswordChecker(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<BreachedPasswordOptions>>().Value;

        return options.Mode switch
        {
            BreachedPasswordMode.Off => new NoBreachedPasswordChecker(),
            BreachedPasswordMode.Range => new KAnonymityRangeChecker(new HttpClient
            {
                BaseAddress = new Uri(options.RangeBaseUrl.TrimEnd('/') + "/", UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(options.RangeTimeoutSeconds),
            }),
            _ => provider.GetRequiredService<TopListPasswordChecker>(),
        };
    }

    private static bool IsVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= LegalDocumentVersions.VersionMaxLength
        && Regex.IsMatch(value, @"^\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
