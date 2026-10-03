using System.Globalization;
using System.Text.RegularExpressions;
using ErrorOr;

namespace NightTales.Domain.Research;

/// <summary>
/// A validated, normalised source URL (02.5.1 plan §3.1, 02 D11): the single place URL syntax is decided, so the Api
/// and the fetcher cannot diverge.
/// </summary>
/// <remarks>
/// <see cref="Parse"/> rejects with <see cref="SourceErrors.SourceUrlInvalid"/>: a scheme other than http/https, an
/// explicit port other than 80/443, any userinfo, more than <see cref="MaxLength"/> characters, an IP-literal host in
/// any notation, an empty host, and a host with no registrable domain. It normalises to the punycode lowercase host
/// without a trailing dot, drops the fragment and a default port, and turns an empty path into <c>/</c>. The query
/// is kept: it often identifies the article.
/// </remarks>
public sealed partial record SourceUrl
{
    /// <summary>The longest accepted URL, as pasted and after normalisation (the <c>normalized_url</c> CHECK).</summary>
    public const int MaxLength = 2_048;

    private const int MaxHostLength = 253;

    private const int MaxLabelLength = 63;

    private SourceUrl(string value, string host, string registrableDomain, bool isHttps)
    {
        Value = value;
        Host = host;
        RegistrableDomain = registrableDomain;
        IsHttps = isHttps;
    }

    /// <summary>The normalised URL, for example <c>https://example.com/a?b=1</c>.</summary>
    public string Value { get; }

    /// <summary>The punycode lowercase host without a trailing dot.</summary>
    public string Host { get; }

    /// <summary>The registrable domain of <see cref="Host"/> (public suffix plus one label).</summary>
    public string RegistrableDomain { get; }

    /// <summary>Whether the scheme is https.</summary>
    public bool IsHttps { get; }

    /// <summary>Parses and normalises <paramref name="input"/>.</summary>
    /// <param name="input">The URL as pasted.</param>
    /// <param name="psl">The public-suffix list that decides the registrable domain.</param>
    /// <returns>The value, or <see cref="SourceErrors.SourceUrlInvalid"/>.</returns>
    public static ErrorOr<SourceUrl> Parse(string? input, IPublicSuffixList psl)
    {
        ArgumentNullException.ThrowIfNull(psl);

        if (string.IsNullOrWhiteSpace(input))
        {
            return SourceErrors.SourceUrlInvalid;
        }

        var text = input.Trim();
        if (text.Length > MaxLength
            || text.Any(char.IsControl)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || HasUserInfo(text, uri)
            || uri.Port is not (80 or 443)
            || uri.HostNameType != UriHostNameType.Dns)
        {
            return SourceErrors.SourceUrlInvalid;
        }

        var host = NormaliseHost(uri.Host);
        if (host is null || LooksLikeIpLiteral(host))
        {
            return SourceErrors.SourceUrlInvalid;
        }

        // A single label (localhost, a bare TLD, a Unicode name that maps to one punycode label) has no registrable domain.
        if (!host.Contains('.', StringComparison.Ordinal) || psl.GetRegistrableDomain(host) is not { Length: > 0 } registrable)
        {
            return SourceErrors.SourceUrlInvalid;
        }

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var defaultPort = isHttps ? 443 : 80;
        var port = uri.Port == defaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var value = $"{uri.Scheme}://{host}{port}{path}{uri.Query}";

        return value.Length > MaxLength
            ? SourceErrors.SourceUrlInvalid
            : new SourceUrl(value, host, registrable, isHttps);
    }

    /// <summary>
    /// Credentials in the authority, however the framework parsed them: the check reads the pasted text between
    /// <c>://</c> and the first path, query or fragment delimiter, so an empty userinfo (<c>https://@host/</c>) is caught too.
    /// </summary>
    private static bool HasUserInfo(string text, Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return true;
        }

        var start = text.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return true;
        }

        var authorityStart = start + 3;
        var end = text.AsSpan(authorityStart).IndexOfAny("/?#\\");
        var authority = end < 0 ? text.AsSpan(authorityStart) : text.AsSpan(authorityStart, end);

        return authority.Contains('@');
    }

    /// <summary>IDNA to punycode, lowercase, one trailing dot stripped; <see langword="null"/> when unusable.</summary>
    private static string? NormaliseHost(string rawHost)
    {
        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(rawHost).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (ascii.EndsWith('.'))
        {
            ascii = ascii[..^1];
        }

        if (ascii.Length is 0 or > MaxHostLength || !HostCharacters().IsMatch(ascii))
        {
            return null;
        }

        return ascii.Split('.').Any(label => label.Length is 0 or > MaxLabelLength) ? null : ascii;
    }

    /// <summary>
    /// A host that is an IP literal in a notation the framework left as a name: all-digit labels, hex labels
    /// (<c>0x7f.0.0.1</c>), a bare integer (<c>2130706433</c>), or a numeric last label (no TLD is numeric).
    /// </summary>
    private static bool LooksLikeIpLiteral(string host)
    {
        if (BareInteger().IsMatch(host))
        {
            return true;
        }

        var labels = host.Split('.');
        return labels.All(label => NumericLabel().IsMatch(label)) || DigitsOnly().IsMatch(labels[^1]);
    }

    [GeneratedRegex("^[a-z0-9_-]+(\\.[a-z0-9_-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostCharacters();

    [GeneratedRegex("^[0-9]{1,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex BareInteger();

    [GeneratedRegex("^(0x[0-9a-f]+|[0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericLabel();

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsOnly();
}
