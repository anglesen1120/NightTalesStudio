using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The shared model pipeline of <see cref="AppDbContext"/> and <see cref="ReadDbContext"/>
/// (09 README D8, C1). Both contexts run exactly this code, so the write and the read model cannot
/// drift apart.
/// </summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// Runs the whole model pipeline for <paramref name="context"/>: the entity configurations, the
    /// Identity mapping and the tenant filter of 09.2.3-T1.
    /// </summary>
    /// <param name="builder">The model being built.</param>
    /// <param name="context">
    /// The context being built. The tenant filter reads its <c>CurrentUserId</c> member, so EF
    /// parameterizes the filter per query instead of baking one scope's user into the model it caches
    /// per context type (09 README D10, plan §2 T2f).
    /// </param>
    public static ModelBuilder ApplyNightTalesConventions(this ModelBuilder builder, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(context);

        builder.ApplyConfigurationsFromAssembly(typeof(ModelBuilderExtensions).Assembly);
        builder.ApplyIdentityModel();
        TenantFilterConvention.Apply(builder, context);

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
            // 09 W3 CR-41: the unique ux_users_normalized_email *replaces* Identity's non-unique
            // email_index (F12/D9) — never a second index beside it.
            b.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ux_users_normalized_email").IsUnique();
            b.ToTable("users");
            b.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(u => u.UserName).HasMaxLength(256);
            b.Property(u => u.NormalizedUserName).HasMaxLength(256);
            b.Property(u => u.Email).HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).HasMaxLength(256);

            // The extra users columns of 09.2.2 plan §3 (owned by 09.2.2-T1).
            b.Property(u => u.DisplayName).HasMaxLength(AppUser.DisplayNameMaxLength).IsRequired();
            b.Property(u => u.UiLocale).HasEnumCheck().HasDefaultValue(UiLocale.Vi);
            b.Property(u => u.Role).HasEnumCheck().HasDefaultValue(PlatformRole.User);
            b.Property(u => u.Status).HasEnumCheck();
            b.Property(u => u.EmailVerifiedAt);
            b.Property(u => u.OwnerDisputeFreezeUntil);
            b.Property(u => u.LastLoginAt);
            b.Property(u => u.LastTotpStep);
            // created_at / updated_at: AppUser : IAuditable, mapped by AuditTimestampConvention.

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
