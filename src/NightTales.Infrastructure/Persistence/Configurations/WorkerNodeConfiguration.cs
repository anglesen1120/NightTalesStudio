using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>worker_nodes</c> table (00 overview §6 L311, 11 README D13, owner 11.3.3-T1): the render-node
/// registry <c>WorkerNodeSeeder</c> upserts, <c>NodeHealthPoller</c> heartbeats and the CLI verbs list and
/// drain.
/// </summary>
/// <remarks>
/// <para>
/// The three value constraints mirror the domain's invariants and the plan's §3 SQL: the state's closed
/// value set comes from 09 README D8's <see cref="EnumCheckExtensions.HasEnumCheck{TEnum}(PropertyBuilder{TEnum})"/>
/// pair, the capacity window is <c>1..8</c>, and <c>tls_cert_sha256</c> is either absent on a plain
/// <c>http://</c> URL (the D14 <c>mock</c> node, 11 README F26/T39) or exactly 64 upper-case hex
/// characters — never a colon-separated fingerprint (D11).
/// </para>
/// <para>
/// There is no tenant filter and no <c>IgnoreQueryFilters</c> allow-list entry: nodes are system data
/// (09 README D10). The only index is the unique <c>name</c> — the row is the registry, and the derived
/// active-task count comes from <c>render_jobs</c>, never from a column here (00 §6 L311).
/// </para>
/// </remarks>
internal sealed class WorkerNodeConfiguration : IEntityTypeConfiguration<WorkerNode>
{
    public void Configure(EntityTypeBuilder<WorkerNode> builder)
    {
        builder.ToTable("worker_nodes");
        builder.HasKey(node => node.Id);

        // UUIDv7 comes from the entity factory, never from the database (D3/D8).
        builder.Property(node => node.Id).ValueGeneratedNever();

        builder.Property(node => node.Name).IsRequired().HasMaxLength(WorkerNode.NameMaxLength);
        builder.Property(node => node.BaseUrl)
            .HasConversion(url => url.ToString(), value => new Uri(value))
            .IsRequired()
            .HasMaxLength(256);

        // bytea, null in P: the settings UI of 09.10 is the S writer (11 README F09).
        builder.Property(node => node.ApiKeyCiphertext);
        builder.Property(node => node.ApiKeyHint).HasMaxLength(16);

        builder.Property(node => node.Capacity).IsRequired();
        builder.Property(node => node.State).HasEnumCheck();
        builder.Property(node => node.LastHeartbeatAt).HasColumnType("timestamptz");
        builder.Property(node => node.GpuModel).HasMaxLength(WorkerNode.GpuModelMaxLength);
        builder.Property(node => node.TlsCertSha256).HasMaxLength(WorkerNode.TlsCertSha256Length);

        builder.Property(node => node.CreatedAt).IsRequired();
        builder.Property(node => node.UpdatedAt).IsRequired();

        builder.HasIndex(node => node.Name)
            .HasDatabaseName("ux_worker_nodes_name")
            .IsUnique();

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_worker_nodes_capacity",
                $"\"capacity\" BETWEEN {WorkerNode.CapacityMin} AND {WorkerNode.CapacityMax}");

            // The pin is absent exactly when the URL is a plain-http one, and present as 64 upper-case hex
            // otherwise — the same rule WorkerNode.Create enforces, kept as the database's last line.
            table.HasCheckConstraint(
                "ck_worker_nodes_tls_sha256",
                "(\"tls_cert_sha256\" IS NULL AND \"base_url\" LIKE 'http://%') "
                + "OR \"tls_cert_sha256\" ~ '^[0-9A-F]{64}$'");
        });
    }
}
