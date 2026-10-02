namespace NightTales.Infrastructure.Persistence.Flags;

/// <summary>
/// The one loaded flag value the read service caches (09.9.2 plan §5.1): the state and the instant it was
/// loaded at, so the 30 s window is measured against the host's clock instead of the cache's own expiry.
/// </summary>
/// <param name="Enabled">The state read from <c>feature_flags.enabled</c>, or <see langword="false"/> for
/// a key no row has.</param>
/// <param name="LoadedAt">When the row was read, from the scope's <c>IClock</c>.</param>
public sealed record FeatureFlagsCacheEntry(bool Enabled, DateTimeOffset LoadedAt);

/// <summary>
/// The vocabulary of the feature-flag read cache (09 README BD13): how an entry is keyed, how the
/// rate-limited warning entry is keyed, and the channel a change is announced on.
/// </summary>
/// <remarks>
/// One definition for the reader, the invalidator and the outbox handler: a key nothing else agrees on
/// would leave a stale entry behind the 30 s window, which is the one failure this mechanism must not
/// have. The prefixes are versioned (<c>v1</c>) so a future entry shape can be introduced without
/// serving a value of the old one.
/// </remarks>
public static class FeatureFlagsCache
{
    /// <summary>The prefix of a cached flag value: <c>flags:v1:{key}</c>.</summary>
    public const string KeyPrefix = "flags:v1:";

    /// <summary>The prefix of the rate-limited warning marker of a key: <c>flags:warn:v1:{key}</c>.</summary>
    public const string WarnKeyPrefix = "flags:warn:v1:";

    /// <summary>
    /// The PostgreSQL channel a flag change is announced on, in the writer's transaction and consumed by
    /// every instance's <c>LISTEN</c> invalidator <b>[RULED — README §B10 WT13]</b>.
    /// </summary>
    public const string ChangedChannel = "feature_flags_changed";

    /// <summary>How long at most one warning is logged per flag key.</summary>
    public static readonly TimeSpan WarningWindow = TimeSpan.FromMinutes(1);

    /// <summary>The cache key of one flag.</summary>
    /// <param name="flagKey">The flag key; it is validated before this is called.</param>
    public static string Key(string flagKey) => KeyPrefix + flagKey;

    /// <summary>The cache key of one flag's warning marker.</summary>
    /// <param name="flagKey">The flag key.</param>
    public static string WarnKey(string flagKey) => WarnKeyPrefix + flagKey;
}
