using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Catalog;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>voices</c> table (09.2.2 plan §3): the curated 04 catalog, keyed by the canonical preset name
/// (a <c>string</c>, 09 W3 CR-41). Platform data, so it is not <c>IUserOwned</c>.
/// </summary>
internal sealed class VoiceConfiguration : IEntityTypeConfiguration<Voice>
{
    public void Configure(EntityTypeBuilder<Voice> builder)
    {
        builder.ToTable("voices");
        builder.HasKey(voice => voice.Id);

        // The 04 D9 id rules (NFC, 1–64 code points) are validated in code by the 04 catalog rules,
        // so the column stays `text` as plan §3 specifies.
        builder.Property(voice => voice.Id);
        builder.Property(voice => voice.DisplayName).IsRequired();
        builder.Property(voice => voice.Language).HasEnumCheck();
        builder.Property(voice => voice.Gender).HasEnumCheck();
        builder.Property(voice => voice.Tags).HasColumnType("text[]");
    }
}
