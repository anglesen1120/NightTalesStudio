namespace NightTales.Infrastructure.Research;

/// <summary>
/// The <c>Fetcher</c> section (02.5.1 plan §3.3). 02.5.1-T1 owns <see cref="BlockedDomains"/>; 02.5.2-T1 adds the
/// fetcher's own keys to this class and to <see cref="FetcherOptionsValidator"/>.
/// </summary>
public sealed class FetcherOptions
{
    /// <summary>The configuration section, <c>Fetcher</c>.</summary>
    public const string SectionName = "Fetcher";

    /// <summary>The blocked registrable domains (02 D11): data, changed only through a reviewed PR.</summary>
    public IList<string> BlockedDomains { get; set; } = [];
}
