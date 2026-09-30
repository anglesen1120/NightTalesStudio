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

        builder.HasOne<Series>().WithMany().HasForeignKey(authorization => authorization.SeriesId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(authorization => authorization.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(authorization => new { authorization.SeriesId, authorization.Version }).IsUnique();
    }
}
