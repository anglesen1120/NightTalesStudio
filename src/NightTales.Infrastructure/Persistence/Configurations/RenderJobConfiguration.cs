using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>render_jobs</c> table (BD5; 00 §6): one row per render attempt, the non-content settings of the render
/// (<c>params</c> = <c>render-params.v1</c>, 05 D11) and the verdict (<c>quality_report</c> = <c>render-quality.v1</c>,
/// 05 D10).
/// </summary>
/// <remarks>
/// <para>
/// Both foreign keys are the same-owner composite keys every child table of a user-owned parent uses
/// (NTS-922-SEC-001, 09 W3 CR-46): <c>(episode_id, user_id)</c> onto the episode's alternate key — which is what makes
/// the episode's own hard delete cascade the row with it (the <c>fact_sheets</c>/<c>fetch_tasks</c> precedent) — and
/// <c>(story_version_id, user_id)</c> onto the version, so a job can never name another user's story.
/// </para>
/// <para>
/// <c>reservation_id</c> is <c>NOT NULL</c> and has no foreign key: the reservation lives in the credit ledger, not in
/// a table of this schema (W2 CR-14, WT50).
/// </para>
/// <para>
/// <c>params</c> keeps <see cref="RenderJob.Params"/> typed (the poller's submit of 09.6.3-T1 reads
/// <c>job.Params</c>) through <see cref="RenderParamsJsonConverter"/>, which writes the record's own
/// <c>[JsonPropertyName]</c> attributes verbatim — that document <em>is</em> <c>render-params.v1</c>. No
/// <c>ValueComparer</c> is needed beside it: the property is <c>init</c>-only, so no save ever replaces it (the
/// <c>series_bibles.content</c> case differs because that document is rewritten in place). <c>quality_report</c> is
/// plain <c>jsonb</c> text (<see cref="RenderJob.QualityReport"/>) because its typed document lives in the Application.
/// </para>
/// <para>
/// <c>Version</c> is the <c>xmin</c> row version (09 README D8, N8) and is mapped by <c>AggregateRootConvention</c>,
/// the tenant filter by <c>TenantFilterConvention</c> and the id's <c>ValueGeneratedNever</c> by
/// <c>EntityIdConvention</c>, so this file declares none of the three.
/// </para>
/// </remarks>
internal sealed class RenderJobConfiguration : IEntityTypeConfiguration<RenderJob>
{
    /// <summary>The BD5 due-job scan behind <c>RenderPollingService</c> (status, then the poll instant).</summary>
    internal const string PollIndexName = "ix_render_jobs_status_next_poll_at";

    /// <summary>BD5's "one active render per episode" partial unique index.</summary>
    internal const string ActiveEpisodeIndexName = "ux_render_jobs_active_episode";

    /// <summary>The tenant-scoped lookups: the jobs of one episode, read newest first by the UUIDv7 id (WT49).</summary>
    internal const string UserEpisodeIndexName = "ix_render_jobs_user_id_episode_id";

    /// <summary>The four statuses BD5 counts as an active render, in the order the index filter lists them.</summary>
    internal const string ActiveStatuses = "\"status\" IN ('queued', 'submitted', 'running', 'ingesting')";

    /// <summary>The two statuses the poller claims (BD5's <c>(status, next_poll_at)</c> index).</summary>
    internal const string PollableStatuses = "\"status\" IN ('submitted', 'running')";

    public void Configure(EntityTypeBuilder<RenderJob> builder)
    {
        builder.ToTable("render_jobs");
        builder.HasKey(job => job.Id);

        // UUIDv7 ids come from the domain factory (D8); EntityIdConvention states the same rule for every entity.
        builder.Property(job => job.Id).ValueGeneratedNever();

        builder.Property(job => job.Status).HasEnumCheck();
        builder.Property(job => job.Stage).HasMaxLength(32).IsRequired();
        builder.Property(job => job.Percent).HasColumnType("smallint");
        builder.Property(job => job.ErrorCode).HasMaxLength(64);
        builder.Property(job => job.ErrorMessage).HasMaxLength(2_000);
        builder.Property(job => job.ReservationId).IsRequired();

        builder.Property(job => job.Params)
            .HasConversion(new RenderParamsJsonConverter())
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(job => job.QualityReport).HasColumnType("jsonb");

        // The poller's due-job scan. The filter keeps the index to the rows a tick can claim.
        builder.HasIndex(job => new { job.Status, job.NextPollAt })
            .HasDatabaseName(PollIndexName)
            .HasFilter(PollableStatuses);

        // One active render per episode: a retry inserts a new row once the previous one is terminal (BD5).
        builder.HasIndex(job => job.EpisodeId)
            .HasDatabaseName(ActiveEpisodeIndexName)
            .IsUnique()
            .HasFilter(ActiveStatuses);

        builder.HasIndex(job => new { job.UserId, job.EpisodeId }).HasDatabaseName(UserEpisodeIndexName);

        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(job => new { job.EpisodeId, job.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StoryVersion>().WithMany()
            .HasForeignKey(job => new { job.StoryVersionId, job.UserId })
            .HasPrincipalKey(version => new { version.Id, version.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(job => job.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// Writes and reads <c>render_jobs.params</c> as the canonical <c>render-params.v1</c> document (05 D11) of
/// <see cref="RenderParamsV1"/>.
/// </summary>
/// <remarks>
/// The record carries a <c>[JsonPropertyName]</c> on every member, so no naming policy is involved and the stored bytes
/// are the D11 document itself; the order is the declaration order, with <c>schema</c> first.
/// </remarks>
internal sealed class RenderParamsJsonConverter : ValueConverter<RenderParamsV1, string>
{
    /// <summary>The one set of settings both directions use: no indentation, every member written (a null <c>music</c> is an explicit <c>null</c>, D11).</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>Creates the converter.</summary>
    public RenderParamsJsonConverter()
        : base(@params => Serialize(@params), json => Parse(json))
    {
    }

    private static string Serialize(RenderParamsV1 @params) => JsonSerializer.Serialize(@params, Options);

    private static RenderParamsV1 Parse(string json) =>
        JsonSerializer.Deserialize<RenderParamsV1>(json, Options)
        ?? throw new InvalidOperationException("A persisted render-params.v1 document is never null: the column is NOT NULL.");
}
