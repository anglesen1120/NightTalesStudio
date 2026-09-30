using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence.Conventions;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>user_consents</c> table (09.3.1-T1, 09 README D9): the acceptance history of 13 FR-13-2.
/// </summary>
/// <remarks>
/// <c>document</c> is enum-as-text with a <c>CHECK</c> from <c>HasEnumCheck</c> (the D8 convention), so
/// only <c>tos</c> and <c>privacy</c> can be stored; <c>accepted_at</c> is <c>timestamptz</c> and
/// <c>ip</c> is <c>inet</c> (Npgsql maps <see cref="System.Net.IPAddress"/> natively). The foreign key
/// is <c>ON DELETE RESTRICT</c>: a user with acceptances cannot be deleted silently — deletion is 13's
/// workflow (13.2.1-T1), not a cascade.
/// </remarks>
internal sealed class UserConsentConfiguration : IEntityTypeConfiguration<UserConsent>
{
    public void Configure(EntityTypeBuilder<UserConsent> builder)
    {
        builder.ToTable("user_consents");
        builder.HasKey(consent => consent.Id);

        // UUIDv7 comes from the entity factory, never from the database (D3/D8).
        builder.Property(consent => consent.Id).ValueGeneratedNever();

        builder.Property(consent => consent.UserId).IsRequired();
        builder.Property(consent => consent.Document).HasEnumCheck();
        builder.Property(consent => consent.Version).HasMaxLength(LegalDocumentVersions.VersionMaxLength).IsRequired();
        builder.Property(consent => consent.AcceptedAt).HasColumnType("timestamptz");
        builder.Property(consent => consent.Ip).HasColumnType("inet");

        builder.HasIndex(consent => new { consent.UserId, consent.Document })
            .HasDatabaseName("ix_user_consents_user_id_document");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(consent => consent.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
