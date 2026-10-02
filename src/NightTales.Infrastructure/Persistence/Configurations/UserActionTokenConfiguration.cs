using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence.Conventions;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>user_action_tokens</c> table (09 README BD3/WF11, 09.3.3-T1): one row per issued single-use
/// token, keyed for the two statements that use it — the unique <c>token_hash</c> a consume resolves and
/// the <c>(user_id, kind)</c> pair an issue invalidates.
/// </summary>
/// <remarks>
/// <para>
/// <c>kind</c> is enum-as-text with a <c>CHECK</c> from
/// <see cref="EnumCheckExtensions.HasEnumCheck{TEnum}(PropertyBuilder{TEnum})"/> (the D8 convention), so
/// only the <c>UserActionKind</c> wire values can be stored and the column stays <c>text</c> exactly as
/// the design's SQL spells it; <c>created_at</c>/<c>expires_at</c>/<c>used_at</c> are <c>timestamptz</c>.
/// </para>
/// <para>
/// The FK cascades to <c>users</c>: a deleted account's links die with it, so a stale reset link can
/// never outlive the account it would unlock.
/// </para>
/// </remarks>
internal sealed class UserActionTokenConfiguration : IEntityTypeConfiguration<UserActionToken>
{
    public void Configure(EntityTypeBuilder<UserActionToken> builder)
    {
        builder.ToTable("user_action_tokens");
        builder.HasKey(token => token.Id);

        // UUIDv7 comes from the issuing statement, never from the database (D3/D8).
        builder.Property(token => token.Id).ValueGeneratedNever();

        builder.Property(token => token.UserId).IsRequired();
        builder.Property(token => token.Kind).HasEnumCheck();
        builder.Property(token => token.SubjectId);
        builder.Property(token => token.TokenHash).IsRequired();
        builder.Property(token => token.CreatedAt).HasColumnType("timestamptz");
        builder.Property(token => token.ExpiresAt).HasColumnType("timestamptz");
        builder.Property(token => token.UsedAt).HasColumnType("timestamptz");

        // A presented token resolves exactly one row; two rows can never share a hash.
        builder.HasIndex(token => token.TokenHash)
            .HasDatabaseName("ux_user_action_tokens_token_hash")
            .IsUnique();

        // The invalidation predicate of an issue: the user's live tokens of one kind.
        builder.HasIndex(token => new { token.UserId, token.Kind })
            .HasDatabaseName("ix_user_action_tokens_user_id_kind");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
