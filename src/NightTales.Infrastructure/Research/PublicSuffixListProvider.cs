using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research;

/// <summary>
/// <see cref="IPublicSuffixList"/> over the embedded <c>public_suffix_list.dat</c> (02.5.1 plan §3.1): the ICANN section
/// only, with the list's algorithm — an exception rule wins, otherwise the longest matching rule (wildcards
/// included), and an unlisted TLD is a one-label suffix. Results are cached by host, at most
/// <see cref="CacheLimit"/> entries.
/// </summary>
/// <remarks>
/// <para>
/// [DECISION] The list is vendored instead of taking the <c>Nager.PublicSuffix</c> package: the list is data, the
/// algorithm is about 40 lines, and the choice adds no dependency to review or keep current. The file's MPL-2.0 header
/// is kept intact (the licence covers the list text).
/// </para>
/// <para>The unlisted-TLD fallback logs the TLD label at debug, never the host (02 D19).</para>
/// </remarks>
public sealed partial class PublicSuffixListProvider : IPublicSuffixList
{
    /// <summary>The logical name of the embedded list.</summary>
    public const string ResourceName = "NightTales.Infrastructure.Research.public_suffix_list.dat";

    /// <summary>The most hosts the cache keeps; past it, results are computed without being stored.</summary>
    public const int CacheLimit = 10_000;

    private readonly HashSet<string> _rules;
    private readonly HashSet<string> _wildcards;
    private readonly HashSet<string> _exceptions;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<PublicSuffixListProvider> _logger;

    /// <summary>Loads the embedded list.</summary>
    /// <param name="logger">The logger.</param>
    public PublicSuffixListProvider(ILogger<PublicSuffixListProvider> logger)
        : this(OpenEmbedded(), logger)
    {
    }

    /// <summary>Parses a list from <paramref name="content"/> (tests pass a small one).</summary>
    /// <param name="content">The <c>.dat</c> text.</param>
    /// <param name="logger">The logger.</param>
    public PublicSuffixListProvider(string content, ILogger<PublicSuffixListProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        (_rules, _wildcards, _exceptions) = Parse(content);
    }

    private PublicSuffixListProvider(Stream stream, ILogger<PublicSuffixListProvider> logger)
        : this(ReadAll(stream), logger)
    {
    }

    /// <inheritdoc />
    public string? GetRegistrableDomain(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (_cache.TryGetValue(host, out var cached))
        {
            return cached;
        }

        var result = Compute(host);
        if (_cache.Count < CacheLimit)
        {
            _cache[host] = result;
        }

        return result;
    }

    private static string ReadAll(Stream stream)
    {
        using (stream)
        using (var reader = new StreamReader(stream))
        {
            return reader.ReadToEnd();
        }
    }

    private static Stream OpenEmbedded() =>
        typeof(PublicSuffixListProvider).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing.");

    private static (HashSet<string> Rules, HashSet<string> Wildcards, HashSet<string> Exceptions) Parse(string content)
    {
        var rules = new HashSet<string>(StringComparer.Ordinal);
        var wildcards = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        var idn = new IdnMapping();
        var inIcann = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("// ===BEGIN ICANN DOMAINS===", StringComparison.Ordinal))
            {
                inIcann = true;
                continue;
            }

            if (line.StartsWith("// ===END ICANN DOMAINS===", StringComparison.Ordinal))
            {
                inIcann = false;
                continue;
            }

            if (!inIcann || line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // A rule is the first whitespace-delimited token of the line.
            var rule = line.Split(' ', '\t')[0].ToLowerInvariant();
            var isException = rule.StartsWith('!');
            var isWildcard = rule.StartsWith("*.", StringComparison.Ordinal);
            var body = isException ? rule[1..] : isWildcard ? rule[2..] : rule;

            string ascii;
            try
            {
                ascii = idn.GetAscii(body);
            }
            catch (ArgumentException)
            {
                continue;
            }

            (isException ? exceptions : isWildcard ? wildcards : rules).Add(ascii);
        }

        return (rules, wildcards, exceptions);
    }

    private string? Compute(string host)
    {
        var labels = host.Split('.');
        var count = labels.Length;

        // An exception rule overrides every other match: the rule itself is the registrable domain.
        for (var index = 0; index < count; index++)
        {
            var candidate = string.Join('.', labels, index, count - index);
            if (_exceptions.Contains(candidate))
            {
                return candidate;
            }
        }

        // The longest rule wins; index 0 is the longest suffix.
        var suffixLabels = 0;
        for (var index = 0; index < count; index++)
        {
            var candidate = string.Join('.', labels, index, count - index);
            var parent = index + 1 < count ? string.Join('.', labels, index + 1, count - index - 1) : string.Empty;
            if (_rules.Contains(candidate) || (parent.Length > 0 && _wildcards.Contains(parent)))
            {
                suffixLabels = count - index;
                break;
            }
        }

        if (suffixLabels == 0)
        {
            // The standard "unlisted TLD" rule: the last label is the suffix.
            LogUnlistedTld(labels[^1]);
            suffixLabels = 1;
        }

        return count <= suffixLabels ? null : string.Join('.', labels, count - suffixLabels - 1, suffixLabels + 1);
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Debug, Message = "psl.unlisted_tld tld={Tld}")]
    private partial void LogUnlistedTld(string tld);
}
