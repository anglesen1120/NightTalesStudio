using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// Registers the single-use action-token store of 09.3.3-T1 (09 README BD3/WF11) and the lifetimes it
/// binds from <c>Auth:ActionToken:*</c>.
/// </summary>
/// <remarks>
/// <para>
/// It is called from the e-mail pipeline's composition root (<c>AddEmail</c>) and not from
/// <c>AddNightTalesAuth</c>, because the reset token is minted at send time by
/// <c>PasswordResetTokenMinter</c> inside <c>SendEmailJob</c> (09 README §B10 WT17/WT30): every host that
/// can render a <c>password_reset</c> message must be able to create its token row, and the Worker has no
/// web signing key and therefore never calls <c>AddNightTalesAuth</c>. Registering it beside the minter
/// keeps the two from ever disagreeing about whether a host can mint.
/// </para>
/// <para>
/// Scoped, like every other store over the scope's <c>AppDbContext</c> and its transaction.
/// </para>
/// </remarks>
public static class UserActionTokenServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IUserActionTokenService"/> and its options validation.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration (<c>Auth:ActionToken:*</c>).</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddUserActionTokens(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<UserActionTokenOptions>()
            .BindConfiguration(UserActionTokenOptions.SectionName)
            .Validate(
                options => options.PasswordResetMinutes > 0,
                $"'{UserActionTokenOptions.SectionName}:PasswordResetMinutes' must be greater than zero (BD3).")
            .Validate(
                options => options.EmailVerifyHours > 0,
                $"'{UserActionTokenOptions.SectionName}:EmailVerifyHours' must be greater than zero (BD3).")
            .ValidateOnStart();

        services.AddScoped<IUserActionTokenService, EfUserActionTokenService>();

        return services;
    }
}
