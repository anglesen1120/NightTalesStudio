using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts one real <c>channel_connections</c> row through the aggregate's own factory, so the encrypted
/// token columns, the scope and the client pin every later publication check reads are production shaped.
/// </summary>
/// <remarks>
/// The status is the one dial a test needs: <see cref="ChannelConnectionStatus.Active"/> is the only state
/// an upload may use, and the other three are reached through the aggregate's own transitions
/// (<c>Revoke</c>/<c>MarkExpired</c>/<c>MarkError</c>), never by writing the column.
/// </remarks>
public sealed class ChannelConnectionBuilder(TestDatabase database, TestCurrentUser currentUser, IClock? clock = null)
{
    /// <summary>The channel title a seeded row carries unless a test names one.</summary>
    public const string DefaultDisplayName = "NTS Test Channel";

    /// <summary>The scope a connect pins, matching what the upload needs (06 README D5).</summary>
    public const string DefaultScope =
        "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    /// <summary>The OAuth client a connect pins (D5; a token minted by another client is never used).</summary>
    public const string DefaultOAuthClientId = "nts-test-client.apps.googleusercontent.com";

    /// <summary>Inserts an <c>active</c> row — the only state a publication create accepts.</summary>
    public async Task<ChannelConnection> CreateActiveAsync(
        Guid userId,
        string? displayName = null,
        CancellationToken ct = default)
        => await CreateAsync(userId, ChannelConnectionStatus.Active, displayName, ct: ct);

    /// <summary>Inserts one row in <paramref name="status"/>.</summary>
    public async Task<ChannelConnection> CreateAsync(
        Guid userId,
        ChannelConnectionStatus status,
        string? displayName = null,
        DateTimeOffset? connectedAt = null,
        CancellationToken ct = default)
    {
        var now = connectedAt ?? clock?.UtcNow ?? DateTimeOffset.UtcNow;

        await using var context = database.CreateAppDbContext(currentUser, clock);

        var connection = ChannelConnection.Connect(
            Guid.CreateVersion7(),
            userId,
            Platform.YouTube,
            // `ux_channel_connections_account` makes (platform, external_account_id) unique among live rows,
            // and one database is shared by every test of a class, so each row names its own channel.
            $"UC-{Guid.CreateVersion7():N}",
            displayName ?? DefaultDisplayName,
            "cfDJ8-nts-test-access-cipher",
            "cfDJ8-nts-test-refresh-cipher",
            now.AddHours(1),
            DefaultScope,
            DefaultOAuthClientId,
            now);

        switch (status)
        {
            case ChannelConnectionStatus.Active:
                break;
            case ChannelConnectionStatus.Revoked:
                connection.Revoke(now);
                break;
            case ChannelConnectionStatus.Expired:
                connection.MarkExpired(now);
                break;
            case ChannelConnectionStatus.Error:
                connection.MarkError(now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown channel connection status.");
        }

        context.Add(connection);
        await context.SaveChangesAsync(ct);

        return connection;
    }
}
