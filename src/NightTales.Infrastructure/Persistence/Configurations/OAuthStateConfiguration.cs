using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Publishing;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>oauth_states</c> table (06.1.2-T1, 06 README D3/D20): one single-use connect attempt per row, claimed
/// by one atomic statement keyed on the 256-bit <c>state_hash</c> capability.
/// </summary>
/// <remarks>
/// <para>
/// There is no navigation and no foreign key to <c>users</c>: the row outlives the session that started the
/// connect and is inert after ten minutes, and the callback has no principal, so the claim reads the row by its
/// explicit <c>state_hash</c> predicate — the presented state is the whole capability — instead of the D10 tenant
/// filter, while the connect-time purge of the caller's own finished rows scopes by <c>user_id</c> (the type is
/// deliberately not <see cref="NightTales.Domain.Common.IUserOwned"/>).
/// </para>
/// <para>
/// <c>platform</c> is <c>text</c> holding the lower-case wire value (<c>youtube</c>, never <c>YouTube</c>), with
/// <c>ck_oauth_states_platform</c> listing the values 06 and 07 can store — the same pair
/// <c>PlatformQuotaUsageConfiguration</c> uses for its own platform counter. The conversion goes through
/// <see cref="PlatformExtensions.ToWire"/>/<see cref="PlatformExtensions.TryParse"/> so the routed value, the
/// stored value and the CHECK cannot drift.
/// </para>
/// <para>
/// The unique <c>state_hash</c> is the claim's access path — one presented state resolves at most one row — and
/// <c>(user_id, created_at DESC)</c> serves the bounded purge of the caller's own finished rows. Because the row
/// is never exposed with an ETag, <see cref="NightTales.Domain.Common.AggregateRoot.Version"/> is not mapped
/// here: the table has no <c>xmin</c> row version and no <c>updated_at</c> column, exactly the shape
/// <c>_06_OAuthStates</c> creates.
/// </para>
/// </remarks>
internal sealed class OAuthStateConfiguration : IEntityTypeConfiguration<OAuthState>
{
    /// <summary>The values <c>ck_oauth_states_platform</c> accepts: 06's platform, and 07's once it ships.</summary>
    internal const string PlatformCheck = "platform IN ('youtube','tiktok')";

    public void Configure(EntityTypeBuilder<OAuthState> builder)
    {
        builder.ToTable(
            "oauth_states",
            table => table.HasCheckConstraint("ck_oauth_states_platform", PlatformCheck));

        builder.HasKey(state => state.Id);

        // UUIDv7 comes from the entity factory, never from the database (D3/D8).
        builder.Property(state => state.Id).ValueGeneratedNever();

        builder.Property(state => state.UserId).IsRequired();
        builder.Property(state => state.Platform)
            .HasConversion(platform => platform.ToWire(), wire => ParsePlatform(wire))
            .HasColumnType("text")
            .IsRequired();
        builder.Property(state => state.StateHash).IsRequired();
        builder.Property(state => state.CodeVerifierProtected).IsRequired();
        // The migration's column name (06 README D20/plan §3.1): the callback's raw SQL addresses
        // `oauth_client_id`, and the snake-case convention would otherwise produce `o_auth_client_id`.
        builder.Property(state => state.OAuthClientId)
            .HasColumnName("oauth_client_id")
            .IsRequired();
        builder.Property(state => state.RefreshFamilyId).IsRequired();
        builder.Property(state => state.BrowserNonceHash).IsRequired();
        builder.Property(state => state.CreatedAt).HasColumnType("timestamptz").IsRequired();
        builder.Property(state => state.ExpiresAt).HasColumnType("timestamptz").IsRequired();
        builder.Property(state => state.UsedAt).HasColumnType("timestamptz");

        // No xmin and no ETag on this row: the claim is a set-based statement that reads used_at itself.
        builder.Ignore(state => state.Version);

        builder.HasIndex(state => state.StateHash)
            .HasDatabaseName("ux_oauth_states_state_hash")
            .IsUnique();

        builder.HasIndex(state => new { state.UserId, state.CreatedAt })
            .HasDatabaseName("ix_oauth_states_user_created")
            .IsDescending(false, true);
    }

    /// <summary>
    /// The inverse of <see cref="PlatformExtensions.ToWire"/>: a stored value outside the closed set is a corrupt
    /// row, because <c>ck_oauth_states_platform</c> refuses to write one.
    /// </summary>
    private static Platform ParsePlatform(string wire) =>
        PlatformExtensions.TryParse(wire, out var platform)
            ? platform
            : throw new InvalidOperationException(
                $"The stored platform '{wire}' is not a platform wire value; ck_oauth_states_platform must refuse such a row.");
}
