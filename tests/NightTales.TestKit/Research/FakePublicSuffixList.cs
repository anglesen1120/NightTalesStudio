using NightTales.Domain.Research;

namespace NightTales.TestKit.Research;

/// <summary>
/// A hand-written <see cref="IPublicSuffixList"/> for Domain tests (02 README D20): a small set of public suffixes
/// and the longest-match rule, with an unlisted TLD treated as a one-label suffix like the real provider.
/// </summary>
public sealed class FakePublicSuffixList : IPublicSuffixList
{
    private static readonly HashSet<string> DefaultSuffixes = new(StringComparer.Ordinal)
    {
        "com", "net", "org", "vn", "com.vn", "co.uk", "uk", "be", "tv", "co", "me", "xn--p1ai",
    };

    private readonly HashSet<string> _suffixes;

    /// <summary>Creates the fake with the default suffix set.</summary>
    public FakePublicSuffixList()
        : this(DefaultSuffixes)
    {
    }

    /// <summary>Creates the fake over <paramref name="suffixes"/>.</summary>
    /// <param name="suffixes">The public suffixes, lowercase.</param>
    public FakePublicSuffixList(IEnumerable<string> suffixes)
    {
        ArgumentNullException.ThrowIfNull(suffixes);

        _suffixes = [.. suffixes];
    }

    /// <inheritdoc />
    public string? GetRegistrableDomain(string host)
    {
        var labels = host.Split('.');
        var suffixLabels = 1;
        for (var index = 0; index < labels.Length; index++)
        {
            if (_suffixes.Contains(string.Join('.', labels, index, labels.Length - index)))
            {
                suffixLabels = labels.Length - index;
                break;
            }
        }

        return labels.Length <= suffixLabels
            ? null
            : string.Join('.', labels, labels.Length - suffixLabels - 1, suffixLabels + 1);
    }
}
