using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Compliance.Deletion;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Compliance.Deletion.Configuration;

/// <summary>
/// The <c>data_deletion_requests</c> table (13.3.1 plan §3.2). No foreign key to <c>users</c>: the row is the evidence
/// that a legal deadline was met and must outlive the account. The open-request unique index carries <c>user_id</c>
/// (13 README YR13) and treats NULL targets as equal, so <c>ON CONFLICT DO NOTHING</c> stays usable.
/// </summary>
internal sealed class DeletionRequestConfiguration : IEntityTypeConfiguration<DeletionRequest>
{
    public void Configure(EntityTypeBuilder<DeletionRequest> builder)
    {
        builder.ToTable("data_deletion_requests");
        builder.HasKey(request => request.Id);

        builder.Property(request => request.Scope).HasEnumCheck();
        builder.Property(request => request.Reason).HasEnumCheck();
        builder.Property(request => request.Status).HasEnumCheck().HasDefaultValue(DeletionStatus.Pending);
        builder.Property(request => request.Attempts).IsRequired().HasDefaultValue(0);
        builder.Property(request => request.LastErrorCode).HasColumnType("text");

        builder.HasIndex(request => new { request.UserId, request.Scope, request.TargetId, request.Reason })
            .HasDatabaseName("ux_deletion_requests_open")
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("completed_at IS NULL");

        builder.HasIndex(request => request.DueAt)
            .HasDatabaseName("ix_deletion_requests_due")
            .HasFilter("completed_at IS NULL");

        builder.HasIndex(request => new { request.UserId, request.RequestedAt, request.Id })
            .HasDatabaseName("ix_deletion_requests_user")
            .IsDescending(false, true, true);

        builder.HasIndex(request => new { request.CompletedAt, request.Id })
            .HasDatabaseName("ix_deletion_requests_reapply")
            .HasFilter("completed_at IS NOT NULL");
    }
}
