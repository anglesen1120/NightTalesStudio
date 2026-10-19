using System.Globalization;
using System.Text.Json;
using NightTales.Domain.Publishing;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts one real <c>publications</c> row straight into the table, for the statuses no factory reaches:
/// the aggregate's own <c>Create</c> only ever produces <c>awaiting_consent</c>, while the episode-detail
/// summary has to be proven over <c>published</c>, <c>scheduled</c>, <c>failed</c> and <c>cancelled</c> rows.
/// </summary>
/// <remarks>
/// <para>
/// The insert is raw SQL on purpose: the column names, the enum wire values and the
/// <c>ux_publications_active_episode_platform</c> predicate are the schema contract of 06.2.1-T1's DDL, and a
/// seed that went through the entity mapper would silently depend on that mapper (the
/// <c>RecentMusicQueryTests</c> precedent for <c>render_jobs</c>).
/// </para>
/// <para>
/// <c>contains_synthetic_media</c> and <c>self_declared_made_for_kids</c> are written by the factory in
/// production (06 README D6); a test that needs a different value passes one.
/// </para>
/// </remarks>
public sealed class PublicationBuilder(TestDatabase database)
{
    /// <summary>The platform column of every seeded row: 06 is YouTube-only (07 writes <c>tiktok</c>).</summary>
    public const string Platform = "youtube";

    /// <summary>The title a seeded row carries unless a test names one.</summary>
    public const string DefaultTitle = "Tập thử nghiệm";

    /// <summary>The description a seeded row carries unless a test names one.</summary>
    public const string DefaultDescription = "Nội dung mô tả thử nghiệm.";

    /// <summary>Inserts one row and returns its id.</summary>
    /// <param name="status">The lifecycle state; <c>awaiting_consent</c> is the factory's own.</param>
    /// <param name="publishedAt">Set only on a <c>published</c> row: what <c>LastPublishedAt</c> reads.</param>
    /// <param name="vetoEndsAt">The autopilot veto window; <c>null</c> for a manual publication.</param>
    public async Task<Guid> CreateAsync(
        Guid userId,
        Guid episodeId,
        Guid channelConnectionId,
        PublicationStatus status = PublicationStatus.AwaitingConsent,
        string? title = null,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        string privacy = "private",
        DateTimeOffset? publishAt = null,
        DateTimeOffset? publishedAt = null,
        DateTimeOffset? vetoEndsAt = null,
        string? platformPayloadJson = null,
        bool containsSyntheticMedia = true,
        DateTimeOffset? createdAt = null,
        CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();
        var stamp = createdAt ?? DateTimeOffset.UtcNow;

        await database.ExecuteAsync(
            "INSERT INTO publications (id, user_id, episode_id, channel_connection_id, platform, status, "
            + "title, description, tags, privacy, publish_at, veto_ends_at, contains_synthetic_media, "
            + "self_declared_made_for_kids, published_at, platform_payload, created_at, updated_at) VALUES ("
            + $"{Literal(id.ToString())}, {Literal(userId.ToString())}, {Literal(episodeId.ToString())}, "
            + $"{Literal(channelConnectionId.ToString())}, {Literal(Platform)}, {Literal(Wire(status))}, "
            + $"{Literal(title ?? DefaultTitle)}, {Literal(description ?? DefaultDescription)}, "
            + $"{Array(tags ?? [])}, {Literal(privacy)}, {Instant(publishAt)}, {Instant(vetoEndsAt)}, "
            + $"{containsSyntheticMedia.ToString().ToLowerInvariant()}, false, {Instant(publishedAt)}, "
            + $"{Json(platformPayloadJson)}, {Instant(stamp)}, {Instant(stamp)})",
            ct);

        return id;
    }

    /// <summary>The snake_case wire value the CHECK constraint lists (<c>awaiting_consent</c> …).</summary>
    public static string Wire(PublicationStatus status) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());

    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Instant(DateTimeOffset? value) =>
        value is null ? "NULL" : Literal(value.Value.ToString("O", CultureInfo.InvariantCulture));

    private static string Json(string? value) => value is null ? "NULL" : $"{Literal(value)}::jsonb";

    private static string Array(IReadOnlyList<string> values) =>
        values.Count == 0 ? "ARRAY[]::text[]" : $"ARRAY[{string.Join(", ", values.Select(Literal))}]::text[]";
}
