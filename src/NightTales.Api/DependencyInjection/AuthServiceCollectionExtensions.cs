using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NightTales.Api.Security;
using NightTales.Infrastructure.Security;

namespace NightTales.Api.DependencyInjection;

/// <summary>
/// Registers the Api's bearer scheme over the platform's ES256 key (09.3.2-T1 step 6, 09 README BD3/W1).
/// </summary>
/// <remarks>
/// <para>
/// The scheme and the issuer share one <see cref="JwtSigningKey"/> singleton, so validation can never
/// disagree with signing about the key or its <c>kid</c>. Validation is strict on purpose: the issuer and
/// the audience must match, only ES256 is accepted (no algorithm can be downgraded), the lifetime is
/// checked with BD3's 30-second skew, and inbound claim mapping is off so the raw <c>sub</c>, <c>role</c>
/// and <c>sid</c> claims reach the Api unchanged — which is what <c>HttpCurrentUser</c> reads.
/// </para>
/// <para>
/// <c>App:PublicOrigin</c> is not bound here: 09.9.4-T1 owns it and binds it, together with the CORS
/// policies built from it, in <see cref="NightTales.Api.Http.SecurityHeadersExtensions"/>. The refresh
/// route's CSRF filter is its first consumer in this story.
/// </para>
/// </remarks>
public static class AuthServiceCollectionExtensions
{
    /// <summary>Registers the bearer scheme and its validation parameters.</summary>
    /// <param name="services">The service collection of the host.</param>
    public static IServiceCollection AddNightTalesJwtBearer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtSigningKey, Microsoft.Extensions.Options.IOptions<JwtOptions>>(
                (bearer, signingKey, jwt) =>
                {
                    bearer.MapInboundClaims = false;
                    bearer.RequireHttpsMetadata = true;
                    bearer.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = jwt.Value.Jwt.Issuer,
                        ValidateAudience = true,
                        ValidAudience = jwt.Value.Jwt.Audience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = signingKey.ValidationKey,
                        RequireSignedTokens = true,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromSeconds(jwt.Value.Access.ClockSkewSeconds),
                        // ES256 only: a token signed with any other algorithm is refused before its claims
                        // are read, so nobody can steer validation onto a weaker algorithm (W2-O3).
                        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                        NameClaimType = HttpCurrentUser.SubjectClaimType,
                        RoleClaimType = HttpCurrentUser.RoleClaimType,
                    };
                });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        return services;
    }
}
