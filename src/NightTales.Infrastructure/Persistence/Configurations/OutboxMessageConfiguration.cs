using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Infrastructure.Persistence.Outbox;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>outbox_messages</c> table (09 README D11, 09.2.2 plan §3). The payload stays an opaque
/// <c>string</c> in a <c>jsonb</c> column — the dispatcher deserializes it by <c>type</c>, so an EF
/// complex type would only add a contract nothing reads (D8, 09 W3 CR-41).
/// </summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(message => message.Id);

        builder.Property(message => message.Type).IsRequired();
        builder.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.Attempts).HasDefaultValue(0);
        builder.Property(message => message.Error).HasMaxLength(OutboxMessage.ErrorMaxLength);
        // The row is added inside SaveChanges, after the auditable interceptor has run, so the column
        // default stamps it (plan §3 maps created_at for every table; there is no ICreatedAt here).
        builder.Property(message => message.CreatedAt).HasDefaultValueSql("now()");

        // Dispatch scan (D11) and cleanup.
        builder.HasIndex(message => message.NextAttemptAt).HasFilter("processed_at IS NULL AND failed_at IS NULL");
        builder.HasIndex(message => message.ProcessedAt);
    }
}
