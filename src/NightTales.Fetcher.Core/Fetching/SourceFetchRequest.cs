namespace NightTales.Fetcher.Core.Fetching;

/// <summary>One fetch the fetcher loop asks for (02.5.2-T1).</summary>
/// <param name="Url">The normalised source URL (re-validated by the fetcher).</param>
/// <param name="RegistrableDomain">The registrable domain of <paramref name="Url"/>: the rate-limit and log key.</param>
/// <param name="Attempt">The attempt number of the task, for logs.</param>
/// <param name="AcceptLanguage">The episode language sent as <c>Accept-Language</c>; <see langword="null"/> uses the configured fallback.</param>
public sealed record SourceFetchRequest(Uri Url, string RegistrableDomain, int Attempt, string? AcceptLanguage = null);
