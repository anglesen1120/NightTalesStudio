using Microsoft.AspNetCore.Identity;

namespace NightTales.Infrastructure.Persistence;

/// <summary>The Identity role of the platform (00 overview §6 <c>roles</c>).</summary>
public sealed class AppRole : IdentityRole<Guid>;
