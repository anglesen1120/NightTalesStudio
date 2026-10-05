namespace NightTales.Application.Abstractions.Similarity;

/// <summary>A corpus could not be read; the message never contains source or storage data.</summary>
public sealed class SimilarityCorpusUnavailableException(string reason)
    : Exception("The similarity corpus is unavailable.")
{
    /// <summary>A value from <see cref="SimilarityUnavailableReason"/>.</summary>
    public string Reason { get; } = reason;
}
