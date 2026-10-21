using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence.Conventions;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>refresh_tokens</c> table (09.3.2-T1, 09 README D9, BD3): one row per issued refresh token,
/// grouped by <c>family_id</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>revoked_reason</c> is enum-as-text with a <c>CHECK</c> from <see cref="EnumCheckExtensions.HasEnumCheck{TEnum}(PropertyBuilder{TEnum})"/>
/// (the D8 convention), so only the nine <c>RevocationReason</c> wire values can be stored;
/// <c>expires_at</c>/<c>created_at</c> are <c>timestamptz</c> and <c>ip</c> is <c>inet</c> (Npgsql maps
/// <see cref="System.Net.IPAddress"/> natively).
/// </para>
/// <para>
/// The two indexes the rotation runs on are the unique <c>token_hash</c> — the presented cookie resolves
/// exactly one row — and the family index the revocation's set-based <c>UPDATE</c> uses. The partial
/// index on <c>expires_at</c> is the access path 09.3.7-T1's session sweep reads.
/// </para>
/// </remarks>
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(token => token.Id);

        // UUIDv7 comes from the entity factory, never from the database (D3/D8).
        builder.Property(token => token.Id).ValueGeneratedNever();

        builder.Property(token => token.UserId).IsRequired();
        builder.Property(token => token.FamilyId).IsRequired();
        builder.Property(token => token.TokenHash).IsRequired();
        builder.Property(token => token.CreatedAt).HasColumnType("timestamptz");
        builder.Property(token => token.LastUsedAt).HasColumnType("timestamptz");
        builder.Property(token => token.ExpiresAt).HasColumnType("timestamptz");
        builder.Property(token => token.RevokedAt).HasColumnType("timestamptz");
        builder.Property(token => token.ReauthenticatedAt).HasColumnType("timestamptz");
        builder.Property(token => token.ReauthLastAttemptAt).HasColumnType("timestamptz");
        builder.Property(token => token.Ip).HasColumnType("inet");

        // `HasEnumCheck` writes the snake_case conversion and the value CHECK together; the column stays
        // `text`, so BD3's 32-character bound is not a column width but the closed value set itself.
        builder.Property(token => token.RevokedReason).HasEnumCheck();
        builder.Property(token => token.ReauthAttempts).HasDefaultValue((short)0);

        builder.HasIndex(token => token.TokenHash)
            .HasDatabaseName("ux_refresh_tokens_token_hash")
            .IsUnique();
        builder.HasIndex(token => token.FamilyId).HasDatabaseName("ix_refresh_tokens_family_id");
        builder.HasIndex(token => token.UserId).HasDatabaseName("ix_refresh_tokens_user_id");
        builder.HasIndex(token => token.ExpiresAt)
            .HasDatabaseName("ix_refresh_tokens_expires_at_live")
            .HasFilter("revoked_at IS NULL");

        // The re-auth counter's clamp (WT32) lives in the increment statement, and this CHECK is what
        // makes an unclamped increment visible instead of silently storing a 6th attempt.
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_refresh_tokens_reauth_attempts",
            "reauth_attempts >= 0 AND reauth_attempts <= 5"));

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The rotation's link to the successor: self-referencing, and SET NULL so deleting an old row can
        // never cascade through the family.
        builder.HasOne<RefreshToken>()
            .WithMany()
            .HasForeignKey(token => token.ReplacedById)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
