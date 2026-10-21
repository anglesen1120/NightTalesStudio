namespace NightTales.Api.RateLimiting;

/// <summary>
/// The rate-limit policy names of 09 README BD14 and 09 W3 CR-7: <c>auth</c>, <c>paid</c>, <c>hub</c>
/// and <c>anon</c>. A route carries one of them through <c>RequireRateLimiting</c>, and 09.3.5-T1 is the
/// one owner that registers them (09 README BD1).
/// </summary>
/// <remarks>
/// <para>
/// The names are the contract the consuming endpoints name in their own code (<c>/api/v1/auth</c>,
/// 02's cost-incurring POSTs, 06's publish POSTs, 09.8.1-T2's <c>POST /hubs/ticket</c>), so they are
/// constants rather than literals at every call site — a misspelled policy would otherwise answer 500 at
/// the first request, because the ASP.NET Core middleware throws for a name that no policy registered.
/// </para>
/// <para>
/// Which key a policy partitions on is part of the policy, not of the endpoint: <c>auth</c> and
/// <c>anon</c> count a resolved IP (an unauthenticated caller has no user), <c>paid</c> and <c>hub</c>
/// count the authenticated user (BD14). <see cref="IsIpKeyed"/> is the single place that decides, so the
/// partition key of a request and the key a rejection logs cannot disagree.
/// </para>
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary><c>auth</c>: 10 requests/minute per resolved IP on the <c>/api/v1/auth</c> routes (BD14).</summary>
    public const string Auth = "auth";

    /// <summary><c>paid</c>: 30 requests/minute per user, sliding window, on cost-incurring POSTs (BD14).</summary>
    public const string Paid = "paid";

    /// <summary><c>hub</c>: 10 tickets/minute per user on <c>POST /api/v1/hubs/ticket</c> (BD14).</summary>
    public const string Hub = "hub";

    /// <summary><c>anon</c>: 60 requests/minute per resolved IP on the anonymous write routes (09 W3 CR-7).</summary>
    public const string Anon = "anon";

    /// <summary>Every policy, in registration order (09 W3 CR-7 adds <see cref="Anon"/> last).</summary>
    public static IReadOnlyList<string> All { get; } = [Auth, Paid, Hub, Anon];

    /// <summary>
    /// Whether <paramref name="policy"/> partitions on the resolved IP rather than on the authenticated
    /// user: <c>auth</c> and <c>anon</c> are IP-keyed, <c>paid</c> and <c>hub</c> are user-keyed (BD14).
    /// </summary>
    /// <param name="policy">A policy name.</param>
    public static bool IsIpKeyed(string policy) => policy is Auth or Anon;

    /// <summary>
    /// Whether <paramref name="policy"/> is a sliding window rather than a fixed one: only <c>paid</c>
    /// is (BD14), which is why only its options carry a segment count.
    /// </summary>
    /// <param name="policy">A policy name.</param>
    public static bool IsSliding(string policy) => policy is Paid;
}
