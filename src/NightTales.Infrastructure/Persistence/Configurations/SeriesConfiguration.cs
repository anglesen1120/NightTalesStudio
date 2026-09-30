using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Catalog;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>The <c>series</c> table (09.2.2 plan §3). The behaviour of the aggregate is 09.4.1-T1's.</summary>
internal sealed class SeriesConfiguration : IEntityTypeConfiguration<Series>
{
    public void Configure(EntityTypeBuilder<Series> builder)
    {
        builder.ToTable("series");
        builder.HasKey(series => series.Id);

        builder.Property(series => series.Title).HasMaxLength(120).IsRequired();
        builder.Property(series => series.Topic).HasEnumCheck();
        builder.Property(series => series.Language).HasEnumCheck();
        builder.Property(series => series.Aspect).HasEnumCheck();
        builder.Property(series => series.VoiceId).IsRequired();
        // story_preset, autopilot_pause_reason: text, nullable.
        builder.Property(series => series.AutopilotEnabled).HasDefaultValue(false);
        // autopilot_settings: the jsonb document 02.8.3-T1 owns the shape of (see the entity property).
        builder.Property(series => series.AutopilotSettings).HasColumnType("jsonb");

        // NTS-922-SEC-001 (09 W3 CR-46): every child table reaches a series through the alternate key
        // (id, user_id), never through `id` alone, so PostgreSQL rejects a row of user B that points at
        // a series of user A. The alternate key is the FK target; the primary key stays `id`.
        builder.HasAlternateKey(series => new { series.Id, series.UserId });

        builder.HasOne<Voice>().WithMany().HasForeignKey(series => series.VoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StyleTemplate>().WithMany().HasForeignKey(series => series.StyleTemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SeriesAutopilotAuthorization>().WithMany()
            .HasForeignKey(series => new { series.CurrentAutopilotAuthorizationId, series.UserId })
            .HasPrincipalKey(authorization => new { authorization.Id, authorization.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(series => series.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(series => new { series.UserId, series.CreatedAt, series.Id }).IsDescending(false, true, true);
        builder.HasIndex(series => series.UserId).HasFilter("archived_at IS NULL");
    }
}
