using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Infrastructure.Persistence.Idempotency;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>idempotency_keys</c> table (09 README D6, 09.1.3 plan §3). The stored response stays an opaque
/// <c>string</c> in a <c>jsonb</c> column: the behaviour serializes it from <c>response_type</c>.
/// </summary>
internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(key => key.Id);

        builder.Property(key => key.Key).HasMaxLength(IdempotencyKey.KeyMaxLength).IsRequired();
        builder.Property(key => key.RequestHash).HasMaxLength(IdempotencyKey.HashLength).IsFixedLength().IsRequired();
        builder.Property(key => key.Response).HasColumnType("jsonb");
        builder.Property(key => key.ExpiresAt).IsRequired();

        builder.HasOne<AppUser>().WithMany().HasForeignKey(key => key.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(key => new { key.UserId, key.Key }).IsUnique();
        builder.HasIndex(key => key.ExpiresAt);
    }
}
