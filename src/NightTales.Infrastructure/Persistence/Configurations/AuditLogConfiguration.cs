using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Audit;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>audit_logs</c> table (09 README BD15, plan §3): append-only rows, one per state-changing
/// command or security event, with the three indexes the readers and the retention sweep walk.
/// </summary>
/// <remarks>
/// <para>
/// The enums are the D8 snake_case text + <c>CHECK</c> pair, <c>data</c> is <c>jsonb</c> with the
/// <c>'{}'</c> default of plan §3, <c>ip</c> is the native <c>inet</c>, and there is deliberately
/// <em>no</em> foreign key to <c>users</c>: a row outlives the account (13 §5.4 hashes the stored id).
/// The one hand-written CHECK makes "a user-visible row without a subject" unrepresentable, which is
/// what keeps the activity feed's predicate (<c>subject_user_id = me</c>) honest.
/// </para>
/// <para>
/// <c>Email</c>-style max lengths are absent on purpose: <c>action</c>, <c>entity_type</c> and
/// <c>user_agent</c> are unbounded <c>text</c> in plan §3, and the values that reach them are codes and
/// attribute literals, not user content (09 README BD16).
/// </para>
/// </remarks>
internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(log => log.Id);

        builder.Property(log => log.Action).IsRequired();
        builder.Property(log => log.ActorType).HasEnumCheck();
        builder.Property(log => log.Category).HasEnumCheck();
        builder.Property(log => log.Visibility).HasEnumCheck();
        builder.Property(log => log.Ip).HasColumnType("inet");

        // Plan §3 fixes the column name: the payload is `data`, not `data_json` (the snake_case
        // convention would derive the latter from the property name).
        builder.Property(log => log.DataJson)
            .HasColumnName("data")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'{}'::jsonb");

        // The owner's activity feed (09.3.6-T2) reads it newest-first for one subject; a user filter
        // never matches the NULL key of a system row, so the same index serves both cases (plan §3).
        builder.HasIndex(log => new { log.SubjectUserId, log.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_logs_subject_created");

        // The per-entity timeline of 09.3.6-T2's `/episodes/{id}/activity`, partial because most rows
        // name no entity.
        builder.HasIndex(log => new { log.EntityType, log.EntityId })
            .HasDatabaseName("ix_audit_logs_entity")
            .HasFilter("entity_id IS NOT NULL");

        // [RULED — README §B10 WT47]: 09.7.3-T1's retention sweep deletes by keyset over
        // (created_at, id), so the sweep has a single b-tree bound per batch and never sorts.
        builder.HasIndex(log => new { log.CreatedAt, log.Id }).HasDatabaseName("ix_audit_logs_created_id");

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_audit_logs_user_visible_has_subject",
            "\"visibility\" <> 'user' OR subject_user_id IS NOT NULL"));
    }
}
