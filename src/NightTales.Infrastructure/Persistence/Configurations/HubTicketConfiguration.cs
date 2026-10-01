using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Realtime;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>hub_tickets</c> table (09.8.1-T2 Design, 09 README BD11/D8): the text hash as the primary
/// key, the owning user with a cascade delete, and the index the hourly sweep walks.
/// </summary>
/// <remarks>
/// <para>
/// No database-generated id and no <c>xmin</c>: the hash is the key and the row is written once, so
/// there is nothing to version. The expiry index is what makes both deletes — the sweep's
/// <c>expires_at &lt; cutoff</c> and this user's opportunistic one — a range scan instead of a
/// sequential read.
/// </para>
/// <para>
/// The claim's <c>UPDATE … WHERE ticket_hash = …</c> needs no index of its own: the primary key is
/// the lookup.
/// </para>
/// </remarks>
internal sealed class HubTicketConfiguration : IEntityTypeConfiguration<HubTicket>
{
    public void Configure(EntityTypeBuilder<HubTicket> builder)
    {
        builder.ToTable("hub_tickets");
        builder.HasKey(ticket => ticket.TicketHash);

        builder.Property(ticket => ticket.TicketHash).HasMaxLength(64).IsRequired();
        builder.Property(ticket => ticket.UserId).IsRequired();
        builder.Property(ticket => ticket.CreatedAt).HasColumnType("timestamptz");
        builder.Property(ticket => ticket.ExpiresAt).HasColumnType("timestamptz");
        builder.Property(ticket => ticket.UsedAt).HasColumnType("timestamptz");

        builder.HasIndex(ticket => ticket.ExpiresAt).HasDatabaseName("ix_hub_tickets_expires_at");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(ticket => ticket.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
