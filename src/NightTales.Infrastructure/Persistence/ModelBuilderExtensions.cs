using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The shared model pipeline of <see cref="AppDbContext"/> and <see cref="ReadDbContext"/>
/// (09 README D8, C1). Both contexts run exactly this code, so the write and the read model cannot
/// drift apart.
/// </summary>
public static class ModelBuilderExtensions
{
    public static ModelBuilder ApplyNightTalesConventions(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(ModelBuilderExtensions).Assembly);
        builder.ApplyIdentityModel();

        return builder;
    }

    /// <summary>
    /// Maps the ASP.NET Core Identity entity types under the 09 README D8 names: <c>users</c>,
    /// <c>roles</c>, <c>user_roles</c>, <c>user_claims</c>, <c>user_logins</c>, <c>user_tokens</c>
    /// and <c>role_claims</c> (the Identity v1 schema — no passkey table, F12: no <c>citext</c>,
    /// e-mail uniqueness comes from the unique <c>normalized_email</c> index).
    /// <see cref="ReadDbContext"/> does not inherit <c>IdentityDbContext</c>, so the same mapping is
    /// applied to both contexts by <see cref="ApplyNightTalesConventions"/>; keep the Identity
    /// schema version at v1/v2 when 09.3.1-T1 registers Identity, or both contexts must move together.
    /// </summary>
    public static ModelBuilder ApplyIdentityModel(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Entity<AppUser>(b =>
        {
            b.HasKey(u => u.Id);
            b.HasIndex(u => u.NormalizedUserName).HasDatabaseName("user_name_index").IsUnique();
            b.HasIndex(u => u.NormalizedEmail).HasDatabaseName("email_index");
            b.ToTable("users");
            b.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(u => u.UserName).HasMaxLength(256);
            b.Property(u => u.NormalizedUserName).HasMaxLength(256);
            b.Property(u => u.Email).HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).HasMaxLength(256);

            b.HasMany<IdentityUserClaim<Guid>>().WithOne().HasForeignKey(uc => uc.UserId).IsRequired();
            b.HasMany<IdentityUserLogin<Guid>>().WithOne().HasForeignKey(ul => ul.UserId).IsRequired();
            b.HasMany<IdentityUserToken<Guid>>().WithOne().HasForeignKey(ut => ut.UserId).IsRequired();
            b.HasMany<IdentityUserRole<Guid>>().WithOne().HasForeignKey(ur => ur.UserId).IsRequired();
        });

        builder.Entity<AppRole>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.NormalizedName).HasDatabaseName("role_name_index").IsUnique();
            b.ToTable("roles");
            b.Property(r => r.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(r => r.Name).HasMaxLength(256);
            b.Property(r => r.NormalizedName).HasMaxLength(256);

            b.HasMany<IdentityUserRole<Guid>>().WithOne().HasForeignKey(ur => ur.RoleId).IsRequired();
            b.HasMany<IdentityRoleClaim<Guid>>().WithOne().HasForeignKey(rc => rc.RoleId).IsRequired();
        });

        builder.Entity<IdentityUserRole<Guid>>(b =>
        {
            b.HasKey(r => new { r.UserId, r.RoleId });
            b.ToTable("user_roles");
        });

        builder.Entity<IdentityUserClaim<Guid>>(b =>
        {
            b.HasKey(uc => uc.Id);
            b.ToTable("user_claims");
        });

        builder.Entity<IdentityUserLogin<Guid>>(b =>
        {
            b.HasKey(l => new { l.LoginProvider, l.ProviderKey });
            b.ToTable("user_logins");
        });

        builder.Entity<IdentityUserToken<Guid>>(b =>
        {
            b.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });
            b.ToTable("user_tokens");
        });

        builder.Entity<IdentityRoleClaim<Guid>>(b =>
        {
            b.HasKey(rc => rc.Id);
            b.ToTable("role_claims");
        });

        return builder;
    }
}
