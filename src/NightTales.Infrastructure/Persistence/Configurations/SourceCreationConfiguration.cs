using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>source_creations</c> table (02 F15/D8, CR-31): the append-only ledger the rolling 24 h url-source cap counts.
/// </summary>
/// <remarks>
/// No foreign key to the episode or the source: the row must survive both deletions, otherwise a delete would free
/// quota. It carries no URL, note or title (D13/D19). The <c>(user_id, created_at)</c> index is the cap query. Every
/// property is <c>ValueGeneratedNever</c>: the domain supplies each value.
/// </remarks>
internal sealed class SourceCreationConfiguration : IEntityTypeConfiguration<SourceCreation>
{
    public void Configure(EntityTypeBuilder<SourceCreation> builder)
    {
        builder.ToTable("source_creations");
        builder.HasKey(creation => creation.Id);

        builder.Property(creation => creation.CreatedAt).HasColumnType("timestamptz");

        builder.HasOne<AppUser>().WithMany().HasForeignKey(creation => creation.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(creation => new { creation.UserId, creation.CreatedAt }).HasDatabaseName("ix_source_creations_user_created");

        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = ValueGenerated.Never;
        }
    }
}
