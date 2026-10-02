using System.Text.RegularExpressions;

namespace NightTales.Application.Abstractions.Flags;

/// <summary>
/// The key shape of a feature flag (09 README BD13, 09.9.2-T2 §Design): a lowercase letter followed by
/// up to 63 more lowercase letters, digits or underscores.
/// </summary>
/// <remarks>
/// <para>
/// One definition, used by every side of the flag mechanism: <see cref="IFeatureFlags"/> answers
/// <see langword="false"/> for an invalid key without a database lookup, the operator CLI rejects one
/// with <c>400 validation_failed</c>, and the cache invalidator ignores a notification that carries
/// anything else. A route whose guard names a typo therefore fails closed instead of probing the table
/// with a key no row can have.
/// </para>
/// <para>
/// <c>09.2.2-T2</c> stores the column as <c>text</c>, so the 64-character bound lives here rather than in
/// a column length: the mechanism's own contract is what enforces it, and no migration is needed to
/// tighten or widen it.
/// </para>
/// </remarks>
public static partial class FeatureFlagKey
{
    /// <summary>The shortest accepted key (a letter and at least one more character).</summary>
    public const int MinLength = 2;

    /// <summary>The longest accepted key.</summary>
    public const int MaxLength = 64;

    /// <summary>The complete-input shape: <c>\A[a-z][a-z0-9_]{1,63}\z</c>, including the length bound.</summary>
    public const string Pattern = @"\A[a-z][a-z0-9_]{1,63}\z";

    /// <summary>Whether <paramref name="key"/> may name a feature flag.</summary>
    /// <param name="key">The candidate key; <see langword="null"/> is invalid.</param>
    public static bool IsValid(string? key) => key is not null && Shape().IsMatch(key);

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
