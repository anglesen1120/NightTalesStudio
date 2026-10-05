using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Similarity;

/// <summary>Immutable scoring parameters for one content language.</summary>
public sealed record SimilarityOptions(Language Language, int N, double WarnAbove, double BlockAbove, int MinTokens)
{
    /// <summary>Selects the configured language window, falling back to English then its default.</summary>
    public static SimilarityOptions For(Language lang, SimilaritySettings settings)
    {
        var code = lang == Language.Vi ? "vi" : "en";
        var n = settings.N.TryGetValue(code, out var configured) ? configured
            : settings.N.TryGetValue("en", out var english) ? english : NFor(Language.En);
        return new SimilarityOptions(lang, n, settings.WarnAbove, settings.BlockAbove, settings.MinTokens);
    }

    /// <summary>The frozen default window sizes: Vietnamese ten, English and other languages eight.</summary>
    public static int NFor(Language lang) => lang == Language.Vi ? 10 : 8;
}
