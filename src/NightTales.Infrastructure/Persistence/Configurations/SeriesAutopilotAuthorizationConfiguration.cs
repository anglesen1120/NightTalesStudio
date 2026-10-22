using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>series_autopilot_authorizations</c> table (09.2.2 plan §3). The behaviour (rendering and
/// accepting an authorization) is 02.8.3-T2's.
/// </summary>
internal sealed class SeriesAutopilotAuthorizationConfiguration : IEntityTypeConfiguration<SeriesAutopilotAuthorization>
{
    public void Configure(EntityTypeBuilder<SeriesAutopilotAuthorization> builder)
    {
        builder.ToTable("series_autopilot_authorizations");
        builder.HasKey(authorization => authorization.Id);

        builder.Property(authorization => authorization.Version).IsRequired();
        builder.Property(authorization => authorization.TextVersion).IsRequired();
        builder.Property(authorization => authorization.TextHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(authorization => authorization.Topic).HasEnumCheck();
        builder.Property(authorization => authorization.Cadence).IsRequired();
        builder.Property(authorization => authorization.PrivacyStatus).HasEnumCheck();

        // 02.8.3 plan §3.7: `revoked_reason`, nullable text over the six wire reasons. The nullable overload
        // of HasEnumCheck keeps the `ck_series_autopilot_authorizations_revoked_reason` constraint in step
        // with the enum and lets NULL (a live row) satisfy it, because `NULL IN (…)` is never false. The
        // column and its constraint arrive with the migration the orchestrator generates after S76 (README
        // D-block D-1); this configuration is the only half of that mapping this task owns.
        builder.Property(authorization => authorization.RevokedReason).HasEnumCheck();

        // NTS-922-SEC-001 (09 W3 CR-46): the same-owner FK to the series (see SeriesConfiguration).
        builder.HasAlternateKey(authorization => new { authorization.Id, authorization.UserId });
        builder.HasOne<Series>().WithMany()
            .HasForeignKey(authorization => new { authorization.SeriesId, authorization.UserId })
            .HasPrincipalKey(series => new { series.Id, series.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(authorization => authorization.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(authorization => new { authorization.SeriesId, authorization.Version }).IsUnique();
    }
}
