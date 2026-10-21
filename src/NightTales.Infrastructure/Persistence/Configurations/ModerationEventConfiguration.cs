using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>moderation_events</c> table (09.2.2 plan §3). Append-only, so it carries only
/// <c>created_at</c>; its rows cascade with the episode.
/// </summary>
internal sealed class ModerationEventConfiguration : IEntityTypeConfiguration<ModerationEvent>
{
    public void Configure(EntityTypeBuilder<ModerationEvent> builder)
    {
        builder.ToTable("moderation_events");
        builder.HasKey(moderationEvent => moderationEvent.Id);

        builder.Property(moderationEvent => moderationEvent.Source).HasEnumCheck();
        builder.Property(moderationEvent => moderationEvent.Stage).IsRequired();
        // The list is read-only on the entity, so EF reads and writes the private _categories field.
        builder.Property(moderationEvent => moderationEvent.Categories)
            .HasColumnType("text[]")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Property(moderationEvent => moderationEvent.Excerpt).HasMaxLength(ModerationEvent.ExcerptMaxLength);

        // NTS-922-SEC-001 (09 W3 CR-46): the same-owner FK to the episode (see SeriesConfiguration).
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(moderationEvent => new { moderationEvent.EpisodeId, moderationEvent.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(moderationEvent => moderationEvent.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(moderationEvent => new { moderationEvent.UserId, moderationEvent.CreatedAt }).IsDescending(false, true);
    }
}
