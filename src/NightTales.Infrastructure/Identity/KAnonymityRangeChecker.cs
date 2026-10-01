using System.Net.Http;
using ErrorOr;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// The k-anonymity breached-password check of <c>Security:BreachedPasswords:Mode = range</c>
/// (09.3.1-T1): only the first <see cref="PasswordRangeKey.PrefixLength"/> hex characters of
/// <c>SHA1(password)</c> leave the server, and the answer is matched locally against the returned
/// suffix list.
/// </summary>
/// <remarks>
/// <para>
/// Fails <em>closed</em>: a timeout, a transport failure or a non-success status maps to 503
/// <c>service_unavailable</c> rather than accepting a possibly-breached password, and the request is
/// never silently retried in <c>off</c> mode. A caller-initiated cancellation still propagates.
/// </para>
/// <para>
/// The client is the one the composition root built (base address and 2 s budget from
/// <c>Security:BreachedPasswords</c>); this type owns it and disposes it with the container.
/// </para>
/// </remarks>
/// <param name="http">The configured client; its base address ends with <c>/</c>.</param>
public sealed class KAnonymityRangeChecker(HttpClient http) : IBreachedPasswordChecker, IDisposable
{
    /// <summary>The stable description of a provider that could not answer (never its body or URL).</summary>
    internal const string Unavailable = "breached_password_source_unavailable";

    /// <summary>
    /// The length of the hex suffix a provider line carries: the SHA-1 digest minus the 5-character
    /// k-anonymity prefix.
    /// </summary>
    private const int SuffixLength = 40 - PasswordRangeKey.PrefixLength;

    /// <inheritdoc />
    public async Task<ErrorOr<bool>> IsBreachedAsync(string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);

        var hash = PasswordRangeKey.Full(password);
        var prefix = hash[..PasswordRangeKey.PrefixLength];
        var suffix = hash[PasswordRangeKey.PrefixLength..];

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"range/{prefix}", UriKind.Relative));
            request.Headers.Add("Add-Padding", "true");

            using var response = await http
                .SendAsync(request, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return UnavailableError();
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return Parse(body, suffix);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            // The provider could not answer inside its budget: fail closed.
            return UnavailableError();
        }
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    /// <summary>
    /// Reads the provider's documented <c>SUFFIX:COUNT</c> body: every non-empty line must be a
    /// <see cref="SuffixLength"/>-character hex suffix, a colon and a decimal count, and the body must
    /// hold at least one entry. A listed suffix counts as breached only when its count is positive:
    /// the <c>Add-Padding</c> protocol answers with count-0 rows for every suffix it does not know
    /// (W3-O3), and those are still valid entries.
    /// </summary>
    /// <remarks>
    /// An empty 200, an HTML error page or a truncated body is an <em>unusable</em> answer, so it fails
    /// closed (503 <c>service_unavailable</c>) instead of reading as "not breached" (QA S2); a
    /// well-formed body that does not list the suffix is a real "not breached".
    /// </remarks>
    private static ErrorOr<bool> Parse(string body, string suffix)
    {
        var entries = 0;
        var matched = false;

        foreach (var line in body.Split('\n'))
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty)
            {
                continue;
            }

            var separator = span.IndexOf(':');
            if (separator != SuffixLength
                || !IsHex(span[..separator])
                || !IsDecimal(span[(separator + 1)..]))
            {
                return UnavailableError();
            }

            entries++;

            if (span[..separator].Equals(suffix, StringComparison.OrdinalIgnoreCase)
                && span[(separator + 1)..].IndexOfAnyExcept('0') >= 0)
            {
                matched = true;
            }
        }

        return entries == 0 ? UnavailableError() : matched;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDecimal(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static Error UnavailableError() =>
        Error.Failure(ErrorCodes.ServiceUnavailable, Unavailable);
}
