namespace NightTales.Domain.Research;

/// <summary>The robots verdict for one URL (02.5.2-T0 spike §2; 02.5.2-T1 decision T43).</summary>
/// <param name="Allowed">Whether the fetch may proceed.</param>
/// <param name="Reason">A short code for logs when denied (never a URL), otherwise <see langword="null"/>.</param>
/// <param name="MinInterval">
/// The per-host interval between requests: <c>max(1 s, crawl-delay)</c>, so the rate limiter takes robots as an input
/// instead of hard-coding one request per second.
/// </param>
public sealed record RobotsDecision(bool Allowed, string? Reason, TimeSpan MinInterval);
