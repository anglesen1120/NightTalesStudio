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

        // 09.10.4-T1 (BD4, WT31/WT35/WT43): the MFA ports. The secret store, the recovery codes, the
        // verifier, the guard and the two flows are scoped because they share the scope's AppDbContext and
        // its transaction; the wrong-code window is a singleton because it is one fact per user for the
        // whole process.
        services.AddScoped<ITotpSecretStore, DataProtectionTotpSecretStore>();
        services.AddScoped<IRecoveryCodeService, CrockfordBase32RecoveryCodes>();
        services.AddScoped<ITotpVerifier, Rfc6238TotpVerifier>();
        services.AddScoped<IReauthGuard, EfReauthGuard>();
        services.AddScoped<ISecurityEventWriter, EfSecurityEventWriter>();

        // 09.2.6-T2: the dashboard's per-request live-account check. Scoped with the AppDbContext it reads
        // (the Hangfire filter resolves it from the request scope, so a singleton filter holds no context).
        services.AddScoped<IJobsDashboardSessionValidator, EfJobsDashboardSessionValidator>();
        services.AddScoped<ISecurityAlerter, AuditAndLogSecurityAlerter>();
        services.AddScoped<IMfaEnrolmentService, EfMfaEnrolmentService>();
        services.AddScoped<IReauthService, EfReauthService>();
        services.AddSingleton<IMfaGuessWindow, InMemoryMfaGuessWindow>();

        // 09.3.2-T3 (BD1/BD4): the challenge store of POST /auth/login, the owner of auth_challenges. It is
        // the single implementation of IMfaChallengeService — there is no interim PendingMfaChallengeService
        // in any commit ([RULED — README §B10 WT39]) — and it is scoped because it shares the scope's
        // AppDbContext and its transaction with the login flow that calls it.
        services.AddScoped<IMfaChallengeService, EfMfaChallengeService>();

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

        services.AddOptions<MfaOptions>()
            .BindConfiguration(MfaOptions.SectionName)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Mfa.Issuer) && options.Mfa.Issuer.Length <= 64,
                $"'{MfaOptions.SectionName}:Mfa:Issuer' is required and at most 64 characters (BD4).")
            .Validate(
                options => options.Mfa.StepSeconds > 0,
                $"'{MfaOptions.SectionName}:Mfa:StepSeconds' must be greater than zero (RFC 6238).")
            .Validate(
                // BD4 fixes the challenge at five minutes; a deployment may tighten it, never widen it, and
                // the pre-auth cookie's Max-Age follows this value.
                options => options.Mfa.ChallengeMinutes > 0
                    && options.Mfa.ChallengeLifetime <= MfaOptions.ChallengeCeiling,
                $"'{MfaOptions.SectionName}:Mfa:ChallengeMinutes' must be in (0, "
                + $"{MfaOptions.ChallengeCeiling.TotalMinutes}] (BD4).")
            .Validate(
                // The auth_challenges CHECK ends at 5, so a wider ceiling could not be stored: the attempt
                // that revokes the challenge must be reachable.
                options => options.Mfa.MaxAttempts == MfaOptions.ChallengeAttemptCeiling,
                $"'{MfaOptions.SectionName}:Mfa:MaxAttempts' must be {MfaOptions.ChallengeAttemptCeiling} (BD4).")
            .Validate(
                // BD4 fixes the window at five minutes: a deployment may tighten it, never widen it.
                options => options.Reauth.WindowMinutes > 0
                    && options.Reauth.WindowMinutes <= MfaOptions.ReauthWindowCeiling.TotalMinutes,
                $"'{MfaOptions.SectionName}:Reauth:WindowMinutes' must be in (0, "
                + $"{MfaOptions.ReauthWindowCeiling.TotalMinutes}] (BD4).")
            .Validate(
                // The column's CHECK ends at 5, so a wider ceiling could not be stored: the attempt that
                // would revoke the family must be reachable.
                options => options.Reauth.MaxAttempts == MfaOptions.ReauthAttemptCeiling,
                $"'{MfaOptions.SectionName}:Reauth:MaxAttempts' must be {MfaOptions.ReauthAttemptCeiling} (BD4).")
            .ValidateOnStart();
    }
}
