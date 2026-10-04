namespace NightTales.Application.Features.Research.Fetcher;

/// <summary>
/// The caps the result route enforces on a fetcher body (02.5.2-T2): a plain object so the Application layer needs no
/// options package; Infrastructure builds it from the validated <c>Fetcher</c> section.
/// </summary>
/// <param name="MaxTextChars">The most characters of extracted text a result may carry (D11: 300,000).</param>
public sealed record FetchResultLimits(int MaxTextChars)
{
    /// <summary>The most proposals a <c>find_sources</c> result may carry (T39); more is a 400.</summary>
    public const int MaxCandidates = 50;

    /// <summary>The longest <c>workerId</c> the internal API accepts.</summary>
    public const int MaxWorkerIdLength = 64;

    /// <summary>The most characters kept of a title or a site name.</summary>
    public const int MaxLabelLength = 300;
}
