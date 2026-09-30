using NightTales.Domain.Common;

namespace NightTales.Domain.Platform;

/// <summary>
/// A platform feature flag (00 overview §6 <c>feature_flags</c>). Platform data, not user-owned, and no
/// <see cref="Entity"/>: the primary key is the flag key. The read path is 09.9.2-T2's, the write path
/// 09.9.2-T1's.
/// </summary>
public sealed class FeatureFlag : IAuditable
{
    /// <summary>The empty rules document every flag starts with (plan §3: <c>rules jsonb default '{}'</c>).</summary>
    public const string EmptyRules = "{}";

    private FeatureFlag()
    {
    }

    public string Key { get; private set; } = string.Empty;

    public bool Enabled { get; private set; }

    /// <summary>The <c>rules</c> JSON document; P answers from <see cref="Enabled"/> only (BD13/WT13).</summary>
    public string Rules { get; private set; } = EmptyRules;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates a flag row; the 09.2.5-T1 seeder inserts one per key, disabled.</summary>
    public static FeatureFlag Create(string key, bool enabled = false, string? rulesJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return new FeatureFlag
        {
            Key = key,
            Enabled = enabled,
            Rules = string.IsNullOrWhiteSpace(rulesJson) ? EmptyRules : rulesJson,
        };
    }
}
