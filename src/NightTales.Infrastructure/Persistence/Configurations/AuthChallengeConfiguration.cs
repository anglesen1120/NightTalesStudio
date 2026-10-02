using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>auth_challenges</c> table (09.3.2-T3, 09 README D9, BD4): one short-lived row per password
/// step that still owes a second factor.
/// </summary>
/// <remarks>
/// <para>
/// The two secrets are unbounded <c>text</c> holding SHA-256 hex only (BD16), <c>ip</c> is the native
/// <c>inet</c> and <c>expires_at</c>/<c>used_at</c> are <c>timestamptz</c> (Npgsql maps
/// <see cref="System.Net.IPAddress"/> natively, like <c>refresh_tokens.ip</c>).
/// </para>
/// <para>
/// The two indexes are the ones the flow runs on: the unique <c>token_hash</c>, which is how the second
/// step resolves exactly one row from the presented <c>mfaToken</c>, and
/// <c>(user_id, expires_at)</c>, which serves the supersede statement of <c>BeginAsync</c> and the
/// expiry-aware reads.
/// </para>
/// </remarks>
internal sealed class AuthChallengeConfiguration : IEntityTypeConfiguration<AuthChallenge>
{
    public void Configure(EntityTypeBuilder<AuthChallenge> builder)
    {
        builder.ToTable("auth_challenges");
        builder.HasKey(challenge => challenge.Id);

        // UUIDv7 comes from the store, never from the database (D3/D8).
        builder.Property(challenge => challenge.Id).ValueGeneratedNever();

        builder.Property(challenge => challenge.UserId).IsRequired();
        builder.Property(challenge => challenge.TokenHash).IsRequired();
        // The spec's column name (09.3.2-T3 Design): the flow's raw SQL addresses `preauth_cookie_hash`,
        // and the snake-case convention would otherwise produce `pre_auth_cookie_hash`.
        builder.Property(challenge => challenge.PreAuthCookieHash)
            .HasColumnName("preauth_cookie_hash")
            .IsRequired();
        builder.Property(challenge => challenge.Attempts).IsRequired().HasDefaultValue((short)0);
        builder.Property(challenge => challenge.ExpiresAt).HasColumnType("timestamptz");
        builder.Property(challenge => challenge.UsedAt).HasColumnType("timestamptz");
        builder.Property(challenge => challenge.Ip).HasColumnType("inet");

        builder.HasIndex(challenge => challenge.TokenHash)
            .HasDatabaseName("ux_auth_challenges_token_hash")
            .IsUnique();
        builder.HasIndex(challenge => new { challenge.UserId, challenge.ExpiresAt })
            .HasDatabaseName("ix_auth_challenges_user_id_expires_at");

        // The row can never hold a sixth attempt, so "at most five wrong codes" is a property of the data
        // and not of one code path (BD4).
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_auth_challenges_attempts",
            "attempts >= 0 AND attempts <= 5"));

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(challenge => challenge.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
