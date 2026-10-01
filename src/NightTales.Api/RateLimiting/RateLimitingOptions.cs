using System.Threading.RateLimiting;

namespace NightTales.Api.RateLimiting;

/// <summary>
/// The rate limiting configuration of 09 README BD14 and 09 W3 CR-7, bound from the
/// <c>RateLimiting</c> section: one <see cref="RateLimitPolicyOptions"/> per policy name, the
/// <c>Retry-After</c> every rejection advertises, and the switch that turns the feature off during an
/// incident.
/// </summary>
/// <remarks>
/// <para>
/// The numbers live here, not in code (09.3.5-T1 Notes): the shipped values are the BD14 defaults and a
/// changed limit is a configuration edit and a redeploy, never a source change. Every member keeps its
/// default when the section is absent, so a host with no <c>RateLimiting</c> section still limits
/// exactly as BD14 says.
/// </para>
/// <para>
/// <see cref="Enabled"/> false is the documented incident lever: every policy is still registered — the
/// ASP.NET Core middleware throws for a policy name that was never registered, so a route carrying
/// <c>RequireRateLimiting("auth")</c> would answer 500 — but each one is a pass-through partition, so no
/// request is rejected. The consequence (no 429 at all) is the point of the switch.
/// </para>
/// </remarks>
public sealed class RateLimitingOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "RateLimiting";

    /// <summary>The <c>Retry-After</c> a rejection advertises when <see cref="RetryAfterSeconds"/> is not set (BD14).</summary>
    public const int DefaultRetryAfterSeconds = 60;

    /// <summary>The shortest window a policy may declare.</summary>
    public const int MinimumWindowSeconds = 1;

    /// <summary>The fewest segments a sliding window may be cut into (a single segment would be a fixed window).</summary>
    public const int MinimumSegmentsPerWindow = 2;

    /// <summary>
    /// The shipped IPv6 prefix an address-keyed partition keeps: /64, the block a residential ISP hands
    /// one subscriber, so rotating the interface address inside it spends one budget (SEC-0935-02).
    /// </summary>
    public const int DefaultIpv6PrefixLength = 64;

    /// <summary>The shortest IPv6 prefix a deployment may key an address partition on.</summary>
    public const int MinimumIpv6PrefixLength = 32;

    /// <summary>The longest IPv6 prefix: the whole 128-bit address.</summary>
    public const int MaximumIpv6PrefixLength = 128;

    /// <summary>The failure a non-positive <c>PermitLimit</c> produces at start-up.</summary>
    public const string NonPositivePermitLimitFailure =
        "'RateLimiting:{auth|paid|hub|anon}:PermitLimit' must be greater than zero.";

    /// <summary>The failure a window below <see cref="MinimumWindowSeconds"/> produces at start-up.</summary>
    public const string TooShortWindowFailure =
        "'RateLimiting:{auth|paid|hub|anon}:WindowSeconds' must be at least 1 second.";

    /// <summary>The failure a <c>RetryAfterSeconds</c> below a policy window produces at start-up.</summary>
    public const string RetryAfterShorterThanWindowFailure =
        "'RateLimiting:RetryAfterSeconds' must be at least every policy's 'WindowSeconds', because the "
        + "value the 429 advertises is the policy window and never a computed remainder (09.3.5-T1 design).";

    /// <summary>The failure an out-of-range <c>SegmentsPerWindow</c> produces at start-up.</summary>
    public const string InvalidSegmentsPerWindowFailure =
        "'RateLimiting:Paid:SegmentsPerWindow' must be at least 2 and at most the policy's 'PermitLimit'.";

    /// <summary>The failure an out-of-range <c>Ipv6PrefixLength</c> produces at start-up.</summary>
    public const string InvalidIpv6PrefixLengthFailure =
        "'RateLimiting:Ipv6PrefixLength' must be between 32 and 128.";

    /// <summary>Whether the limiter is applied at all (a pass-through when false); true by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The IPv6 prefix length an address-keyed partition keeps (<see cref="DefaultIpv6PrefixLength"/>,
    /// /64 by default). An IPv6 subscriber controls a whole /64, so a longer prefix would let it rotate
    /// its interface address for a fresh budget; a shorter one would pool unrelated subscribers.
    /// </summary>
    public int Ipv6PrefixLength { get; set; } = DefaultIpv6PrefixLength;

    /// <summary>
    /// The seconds every rejection advertises through <c>Retry-After</c> and <c>retryAfterSeconds</c>.
    /// The ASP.NET Core rejection callback exposes no reset instant (09 README W8), so this is the
    /// configured window — never a computed, possibly negative, remainder.
    /// </summary>
    public int RetryAfterSeconds { get; set; } = DefaultRetryAfterSeconds;

    /// <summary>The <c>auth</c> policy: 10 requests per 60 s per resolved IP (BD14).</summary>
    public RateLimitPolicyOptions Auth { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    /// <summary>The <c>paid</c> policy: 30 requests per 60 s per user, sliding in 6 segments (BD14).</summary>
    public RateLimitPolicyOptions Paid { get; set; } = new() { PermitLimit = 30, WindowSeconds = 60, SegmentsPerWindow = 6 };

    /// <summary>The <c>hub</c> policy: 10 tickets per 60 s per user (BD14).</summary>
    public RateLimitPolicyOptions Hub { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    /// <summary>The <c>anon</c> policy: 60 requests per 60 s per resolved IP (09 W3 CR-7).</summary>
    public RateLimitPolicyOptions Anon { get; set; } = new() { PermitLimit = 60, WindowSeconds = 60 };

    /// <summary>The options of <paramref name="policy"/>.</summary>
    /// <param name="policy">A name of <see cref="RateLimitPolicies.All"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The name is not a registered policy.</exception>
    public RateLimitPolicyOptions For(string policy) => policy switch
    {
        RateLimitPolicies.Auth => Auth,
        RateLimitPolicies.Paid => Paid,
        RateLimitPolicies.Hub => Hub,
        RateLimitPolicies.Anon => Anon,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown rate-limit policy."),
    };

    /// <summary>Whether every policy's <c>PermitLimit</c> is positive.</summary>
    internal bool HasPositivePermitLimits() =>
        EveryPolicy(static options => options.PermitLimit > 0);

    /// <summary>Whether every policy's window is at least <see cref="MinimumWindowSeconds"/>.</summary>
    internal bool HasWindowsAtLeastOneSecond() =>
        EveryPolicy(static options => options.WindowSeconds >= MinimumWindowSeconds);

    /// <summary>Whether every policy's window is no longer than <see cref="RetryAfterSeconds"/>.</summary>
    internal bool RetryAfterCoversEveryWindow() =>
        RetryAfterSeconds > 0 && EveryPolicy(options => options.WindowSeconds <= RetryAfterSeconds);

    /// <summary>Whether the sliding policy's segment count is one the sliding limiter accepts.</summary>
    internal bool HasValidSegmentsPerWindow() =>
        Paid.SegmentsPerWindow >= MinimumSegmentsPerWindow && Paid.SegmentsPerWindow <= Paid.PermitLimit;

    /// <summary>Whether the IPv6 prefix length is one an address key can mask with.</summary>
    internal bool HasValidIpv6PrefixLength() =>
        Ipv6PrefixLength >= MinimumIpv6PrefixLength && Ipv6PrefixLength <= MaximumIpv6PrefixLength;

    private bool EveryPolicy(Func<RateLimitPolicyOptions, bool> predicate) =>
        predicate(Auth) && predicate(Paid) && predicate(Hub) && predicate(Anon);
}

/// <summary>
/// The window of one rate-limit policy (09 README BD14): how many permits a window admits, how long the
/// window is, and — for the sliding policy only — how many segments the window is cut into.
/// </summary>
public sealed class RateLimitPolicyOptions
{
    /// <summary>The permits the window admits.</summary>
    public int PermitLimit { get; set; }

    /// <summary>The window's length in seconds.</summary>
    public int WindowSeconds { get; set; }

    /// <summary>
    /// The segments a sliding window is cut into (BD14's <c>paid</c> policy uses 6); a fixed window
    /// ignores it.
    /// </summary>
    public int SegmentsPerWindow { get; set; }

    /// <summary>The window as a duration.</summary>
    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);

    /// <summary>
    /// This policy's window as a fixed-window limiter (BD14's <c>auth</c>, <c>hub</c> and <c>anon</c>):
    /// no queue — a rejected request must fail immediately, never wait for a permit — and automatic
    /// replenishment, which is what advances the window.
    /// </summary>
    public FixedWindowRateLimiterOptions ToFixedWindowOptions() => new()
    {
        PermitLimit = PermitLimit,
        Window = Window,
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    /// <summary>This policy's window as a sliding-window limiter (BD14's <c>paid</c>), with the same no-queue rule.</summary>
    public SlidingWindowRateLimiterOptions ToSlidingWindowOptions() => new()
    {
        PermitLimit = PermitLimit,
        Window = Window,
        SegmentsPerWindow = SegmentsPerWindow,
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
