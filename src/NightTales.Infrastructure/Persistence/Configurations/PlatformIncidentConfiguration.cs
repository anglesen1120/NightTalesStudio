using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Publishing;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>platform_incidents</c> table (06.2.3-T2, 06 README D14/D20, plan §3.3): one row per OAuth client the platform
/// refused, closed by an operator. Platform-wide data, so the type is not <c>IUserOwned</c> and no tenant filter
/// applies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly plan §3.3's shape.</b> <c>id</c> (UUIDv7, from the raising caller), <c>platform</c>,
/// <c>oauth_client_id</c>, <c>error_code</c>, <c>raised_at</c>, <c>cleared_at</c>, <c>cleared_by</c> and <c>note</c> —
/// no audit columns (the table is not <c>IAuditable</c>), no soft-delete column and no row version, so
/// <see cref="NightTales.Domain.Common.AggregateRoot.Version"/> never applies (the type derives from
/// <see cref="NightTales.Domain.Common.Entity"/>). Every timestamp is <c>timestamptz</c> through the model's
/// <c>DateTimeOffset</c> convention (both the non-nullable <c>raised_at</c> and the nullable <c>cleared_at</c>).
/// </para>
/// <para>
/// <b><c>ux_platform_incidents_open</c> is the idempotency of the raise.</b> The partial unique index on
/// <c>(platform, oauth_client_id) WHERE cleared_at IS NULL</c> makes "one open incident per client" a storage fact, is
/// the arbiter of <c>INSERT … ON CONFLICT DO NOTHING</c> in <c>PlatformIncidentStore.RaiseAsync</c>, and serves
/// <c>HasOpenIncidentAsync</c> as a single index probe (plan §3.4). A cleared row leaves the index, so the same client
/// can raise a new incident later.
/// </para>
/// <para>
/// <b><c>ix_platform_incidents_raised_at</c> is descending</b> (plan §3.3): the operator's alert path reads the newest
/// incident first.
/// </para>
/// <para>
/// <b><c>oauth_client_id</c> is pinned by name:</b> the snake-case convention would spell the CLR member
/// <c>OAuthClientId</c> as <c>o_auth_client_id</c>, and plan §3.3 (with <c>channel_connections</c>) pins the column
/// spelling.
/// </para>
/// </remarks>
internal sealed class PlatformIncidentConfiguration : IEntityTypeConfiguration<PlatformIncident>
{
    /// <summary>The partial unique index that makes "one open incident per client" a storage fact (plan §3.3).</summary>
    internal const string OpenIndexName = "ux_platform_incidents_open";

    /// <summary>The predicate of <see cref="OpenIndexName"/>: only open rows compete.</summary>
    internal const string OpenRowFilter = "cleared_at IS NULL";

    /// <summary>The newest-first index the operator's alert read uses (plan §3.3).</summary>
    internal const string RaisedAtIndexName = "ix_platform_incidents_raised_at";

    public void Configure(EntityTypeBuilder<PlatformIncident> builder)
    {
        builder.ToTable("platform_incidents");

        builder.HasKey(incident => incident.Id);

        builder.Property(incident => incident.Platform).IsRequired();

        builder.Property(incident => incident.OAuthClientId)
            .HasColumnName("oauth_client_id")
            .IsRequired();

        builder.Property(incident => incident.ErrorCode).IsRequired();
        builder.Property(incident => incident.RaisedAt).IsRequired();

        // Nullable by definition: an open incident has none of the three (that is the index's predicate), and only
        // 06.2.3-T3's clear command writes them.
        builder.Property(incident => incident.ClearedAt);
        builder.Property(incident => incident.ClearedBy);
        builder.Property(incident => incident.Note);

        // A computed member, not a column: `cleared_at IS NULL` already holds the same fact.
        builder.Ignore(incident => incident.IsOpen);

        builder.HasIndex(incident => new { incident.Platform, incident.OAuthClientId })
            .HasDatabaseName(OpenIndexName)
            .IsUnique()
            .HasFilter(OpenRowFilter);

        builder.HasIndex(incident => incident.RaisedAt)
            .HasDatabaseName(RaisedAtIndexName)
            .IsDescending();
    }
}
