using Microsoft.AspNetCore.Identity;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The Identity user of the platform (00 overview §6 <c>users</c>). The extra columns (role, status,
/// ui_locale, display_name, two_factor_enabled, …) and their configuration are owned by 09.2.2-T1.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>;
