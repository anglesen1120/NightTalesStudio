using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>channel_connections</c> table (06.1.2-T2, 06 README D3/D4/D5/D18/D20): one owned, publishable YouTube
/// channel per row, with both tokens as <c>ITokenProtector</c> ciphertext and the OAuth client that minted the
/// code pinned on the row.
/// </summary>
/// <remarks>
/// <para>
/// There is no navigation and no foreign key to <c>users</c>: the row outlives the account (13 §5.4, the
/// <c>email_messages</c> precedent of 09.2.6-T3), and the deletion engine's channel-scope handler nulls the
/// identity columns through <see cref="ChannelConnection.PurgeYouTubeData"/> instead of cascading. The tenant
/// filter itself is <c>TenantFilterConvention</c>'s, because the type is
/// <see cref="NightTales.Domain.Common.IUserOwned"/>.
/// </para>
/// <para>
/// <c>platform</c> and <c>status</c> are <c>text</c> holding the lower-case wire value (<c>youtube</c>,
/// <c>revoked</c>) with a <c>CHECK</c> each. <c>status</c> goes through <c>HasEnumCheck</c>, whose convention
/// emits <c>ck_channel_connections_status</c> with exactly the enum's four snake_case values.
/// <c>platform</c> is declared here with the same pair <c>OAuthStateConfiguration</c> uses, because the column
/// has to admit 07's <c>tiktok</c> before the enum declares it; the conversion goes through
/// <see cref="PlatformExtensions.ToWire"/>/<see cref="PlatformExtensions.TryParse"/> so the routed value, the
/// stored value and the CHECK cannot drift.
/// </para>
/// <para>
/// The two indexes are README D18's. <c>ux_channel_connections_account</c> is the partial unique index that
/// makes "one live owner per Google channel" a storage fact and is also the upsert's read path; the
/// <c>WHERE status &lt;&gt; 'revoked'</c> predicate is what lets a channel be connected by someone else after a
/// revoke, and it is why a purged row (whose <c>external_account_id</c> is <c>NULL</c>) can never collide with a
/// live one. <c>ix_channel_connections_user_id</c> serves the tenant-filtered <c>GET /channels</c> list.
/// </para>
/// <para>
/// <c>Version</c> is deliberately not mapped: this resource carries no ETag (<c>If-Match</c> is 06.2.1-T1's
/// <c>ETags.Publication</c>), so the table has no <c>xmin</c> row version and no <c>updated_at</c> column.
/// </para>
/// </remarks>
internal sealed class ChannelConnectionConfiguration : IEntityTypeConfiguration<ChannelConnection>
{
    /// <summary>The values <c>ck_channel_connections_platform</c> accepts: 06's platform, and 07's once it ships.</summary>
    internal const string PlatformCheck = "platform IN ('youtube','tiktok')";

    /// <summary>The partial predicate of <c>ux_channel_connections_account</c>: only live rows compete.</summary>
    internal const string LiveRowFilter = "status <> 'revoked'";

    /// <summary>The unique index that makes "one live owner per Google channel" (06 README D3/D18).</summary>
    internal const string AccountIndexName = "ux_channel_connections_account";

    /// <summary>The tenant-scoped list index behind <c>GET /channels</c> (06 README D18).</summary>
    internal const string UserIndexName = "ix_channel_connections_user_id";

    public void Configure(EntityTypeBuilder<ChannelConnection> builder)
    {
        builder.ToTable(
            "channel_connections",
            table => table.HasCheckConstraint("ck_channel_connections_platform", PlatformCheck));

        builder.HasKey(connection => connection.Id);

        // UUIDv7 comes from the caller and is pinned by the factory, never from the database (06 README D4).
        builder.Property(connection => connection.Id).ValueGeneratedNever();

        builder.Property(connection => connection.UserId).IsRequired();

        builder.Property(connection => connection.Platform)
            .HasConversion(platform => platform.ToWire(), wire => ParsePlatform(wire))
            .HasColumnType("text")
            .IsRequired();

        // Google's channel id: NULL only after PurgeYouTubeData nulled an already-revoked row.
        builder.Property(connection => connection.ExternalAccountId).HasMaxLength(64);

        // The channel title: NULL only after the same purge, and refreshed by 06.4.2-T1's 25-day check.
        builder.Property(connection => connection.DisplayName).HasMaxLength(200);

        builder.Property(connection => connection.Status).HasEnumCheck();

        // Both token columns are `text NULL` by convention: Revoke nulls the pair, and their lengths depend on
        // the protector's format, so no width is declared. The purpose is tokens.v1/{Id} (06 README D4).
        builder.Property(connection => connection.TokenExpiresAt).HasColumnType("timestamptz").IsRequired();
        builder.Property(connection => connection.Scope).HasMaxLength(512).IsRequired();

        // The migration's column name (06 README D20/plan §3.1): the snake-case convention would otherwise
        // produce `o_auth_client_id`, and D5/YR11 pin the client on this row.
        builder.Property(connection => connection.OAuthClientId)
            .HasColumnName("oauth_client_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(connection => connection.LastVerifiedAt).HasColumnType("timestamptz");
        builder.Property(connection => connection.ConnectedAt).HasColumnType("timestamptz").IsRequired();
        builder.Property(connection => connection.RevokedAt).HasColumnType("timestamptz");

        // No xmin and no ETag on this row: nothing here is an If-Match target (README D15).
        builder.Ignore(connection => connection.Version);

        // One live owner per (platform, external_account_id): the upsert's read path and the race's authority.
        // A revoked row leaves the index, so its channel can be connected by another user afterwards.
        builder.HasIndex(connection => new { connection.Platform, connection.ExternalAccountId })
            .HasDatabaseName(AccountIndexName)
            .IsUnique()
            .HasFilter(LiveRowFilter);

        builder.HasIndex(connection => connection.UserId).HasDatabaseName(UserIndexName);
    }

    /// <summary>
    /// The inverse of <see cref="PlatformExtensions.ToWire"/>: a stored value outside the closed set is a corrupt
    /// row, because <c>ck_channel_connections_platform</c> refuses to write one.
    /// </summary>
    private static Platform ParsePlatform(string wire) =>
        PlatformExtensions.TryParse(wire, out var platform)
            ? platform
            : throw new InvalidOperationException(
                $"The stored platform '{wire}' is not a platform wire value; ck_channel_connections_platform must refuse such a row.");
}
