using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.Security;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the token and session services of 09.3.2-T1 (09 README BD1/BD3): the ES256 issuer, the
/// refresh-token store, the credential flows and the two options types their start-up validation reads.
/// </summary>
/// <remarks>
/// <para>
/// Called by the Api's composition root, <em>not</em> by <c>AddPersistence</c>: the platform's tokens are
/// the web host's concern, and the Worker — which also calls <c>AddPersistence</c> — must not be forced to
/// carry a web signing key to start. The Worker reaches the same table only through jobs that name it
/// explicitly.
/// </para>
/// <para>
/// Everything here fails at start-up rather than per request: a missing or malformed signing key, an
/// owner/admin session policy wider than BD3, or a refresh-cookie name without the <c>__Secure-</c> prefix
/// stops the host. That is the point of binding the two options types with <c>ValidateOnStart</c> — a host
/// that cannot sign a correct token, or that would weaken a session rule, must not serve traffic.
/// </para>
/// </remarks>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>Registers the auth services and their options validation.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration (<c>Auth:*</c>).</param>
    public static IServiceCollection AddNightTalesAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddOptions(services);

        // The key is loaded once per process and shared by signing and by the Api's bearer validation, so
        // the two cannot disagree. The options validation above already refused an unusable key.
        services.AddSingleton(provider => JwtSigningKey.FromPem(
            provider.GetRequiredService<IOptions<JwtOptions>>().Value.Jwt.SigningKey));

        services.AddSingleton<ITokenIssuer, Es256TokenIssuer>();

        // 09.3.2-T1's two counters, on one meter for the process (09.9.3-T1 exports it).
        services.AddSingleton<AuthMetrics>();

        // One instance owns the table: the store's writes and the lookup's indexed read are the same type,
        // exactly as the idempotency protocol keeps its claim and its reset together.
        services.AddScoped<EfRefreshTokenService>();
        services.AddScoped<IRefreshTokenService>(provider => provider.GetRequiredService<EfRefreshTokenService>());
        services.AddScoped<IRefreshTokenLookup>(provider => provider.GetRequiredService<EfRefreshTokenService>());

        // Scoped: the flow reads AppUser through the scope's context and writes last_login_at through it.
        services.AddScoped<IAuthSessionService, RefreshSessionService>();

        return services;
    }

    private static void AddOptions(IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            // BD3's default issuer is `nts-{environment}`: resolved here so both the issuer and the bearer
            // scheme read one concrete value instead of each re-inventing the fallback.
            .PostConfigure<IHostEnvironment>((options, environment) =>
            {
                if (string.IsNullOrWhiteSpace(options.Jwt.Issuer))
                {
                    options.Jwt.Issuer = $"nts-{environment.EnvironmentName}";
                }
            })
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Jwt.Issuer),
                $"'{JwtOptions.SectionName}:Jwt:Issuer' is required (defaults to nts-<environment>).")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Jwt.Audience),
                $"'{JwtOptions.SectionName}:Jwt:Audience' is required (defaults to {JwtOptions.DefaultAudience}).")
            .Validate(
                options => options.Access.AdminTokenMinutes > 0 && options.Access.UserTokenMinutes > 0,
                $"'{JwtOptions.SectionName}:Access:AdminTokenMinutes' and ':UserTokenMinutes' must be greater than zero (BD3).")
            .Validate(
                options => options.Access.ClockSkewSeconds >= 0,
                $"'{JwtOptions.SectionName}:Access:ClockSkewSeconds' must not be negative (BD3).")
            .Validate(
                options => JwtSigningKey.TryCreate(options.Jwt.SigningKey, out _, out _),
                JwtSigningKey.ConfigurationFailure)
            .ValidateOnStart();

        services.AddOptions<SessionOptions>()
            .BindConfiguration(SessionOptions.SectionName)
            .Validate(
                // BD3's ceiling from the role's first row: the idle window below is the tighter of the two,
                // and the absolute value may not exceed 24 h, which is the documented outer bound.
                options => options.Session.OwnerAbsoluteHours > 0
                    && options.Session.OwnerAbsoluteHours <= SessionOptions.OwnerAbsoluteCeiling.TotalHours,
                $"'{SessionOptions.SectionName}:Session:OwnerAbsoluteHours' must be in (0, "
                + $"{SessionOptions.OwnerAbsoluteCeiling.TotalHours}] (BD3).")
            .Validate(
                options => options.Session.OwnerIdleMinutes > 0
                    && options.Session.OwnerIdleMinutes <= SessionOptions.OwnerIdleCeiling.TotalMinutes,
                $"'{SessionOptions.SectionName}:Session:OwnerIdleMinutes' must be in (0, "
                + $"{SessionOptions.OwnerIdleCeiling.TotalMinutes}] (BD3).")
            .Validate(
                options => options.Session.UserAbsoluteDays > 0,
                $"'{SessionOptions.SectionName}:Session:UserAbsoluteDays' must be greater than zero (BD3).")
            .Validate(
                // A cookie without the prefix loses the browser's own prefix protection, so a deployment
                // cannot quietly rename the refresh cookie into something weaker (BD3).
                options => options.Refresh.CookieName.StartsWith(SessionOptions.SecurePrefix, StringComparison.Ordinal)
                    && options.Refresh.CookieName.Length > SessionOptions.SecurePrefix.Length,
                $"'{SessionOptions.SectionName}:Refresh:CookieName' must keep the '{SessionOptions.SecurePrefix}' prefix (BD3).")
            .ValidateOnStart();
    }
}
