using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The tracked write model (09 README D8). It is the only context that owns migrations and the only
/// one who saves changes; the tenant filter of 09.2.3-T1 reads <see cref="CurrentUserId"/>.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IAppDbContext
{
    internal Guid? CurrentUserId { get; } = currentUser.Id;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.ApplyNightTalesConventions();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyNightTalesConventions();
    }
}
