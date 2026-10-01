using NightTales.Application.Abstractions.Identity;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// <c>Security:BreachedPasswords</c> configuration (09.3.1-T1, 09.3-auth/plan.md §5).
/// </summary>
/// <remarks>
/// <para>
/// <c>Mode</c> defaults to <see cref="BreachedPasswordMode.List"/>: the bundled list needs no network
/// and no privacy decision; the range mode with list fallback arrives with 09.3.1-T2 (09 W3-O3).
/// </para>
/// <para>
/// <c>RangeBaseUrl</c>/<c>RangeTimeoutSeconds</c> are [INFERENCE] names: 09.3.1-T1 fixes the 2 s budget
/// and the fail-closed behaviour, not the key names, and 09.3.1-T2 (S) replaces this client with the
/// resilience-pipeline one. Both values are ignored in <c>list</c>/<c>off</c> mode.
/// </para>
/// </remarks>
public sealed class BreachedPasswordOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Security:BreachedPasswords";

    /// <summary>The provider the range mode talks to when no base URL is configured.</summary>
    public const string DefaultRangeBaseUrl = "https://api.pwnedpasswords.com";

    /// <summary>The provider budget in seconds when none is configured.</summary>
    public const int DefaultRangeTimeoutSeconds = 2;

    /// <summary>Which source answers <c>breached_password</c>; <c>list</c> in P.</summary>
    public BreachedPasswordMode Mode { get; set; } = BreachedPasswordMode.List;

    /// <summary>How many ranked entries of the bundled list are honoured. Ignored outside <c>list</c>.</summary>
    public int TopListSize { get; set; } = 100_000;

    /// <summary>The range provider's base URL. Ignored outside <c>range</c>.</summary>
    public string RangeBaseUrl { get; set; } = DefaultRangeBaseUrl;

    /// <summary>The range provider's request budget. Ignored outside <c>range</c>.</summary>
    public int RangeTimeoutSeconds { get; set; } = DefaultRangeTimeoutSeconds;
}
