using Microsoft.Extensions.Configuration;

namespace NightTales.Api.Http;

/// <summary>
/// The request-body cap of the host (09 README BD12, 09.9.4-T1): one default for every endpoint, bound to
/// the <c>Kestrel:Limits</c> section.
/// </summary>
/// <remarks>
/// <para>
/// The value is applied twice, on purpose, and both uses read it from here. Kestrel's own
/// <c>Limits.MaxRequestBodySize</c> is set from it, which is what stops a declared or streamed body that
/// is too large while it is still being read; and the API's request-limit middleware sets the same number
/// on the per-request <c>IHttpMaxRequestBodySizeFeature</c> and refuses an oversized <c>Content-Length</c>
/// before the handler runs, which is what makes the cap testable and server-independent (a test host is
/// not Kestrel and enforces nothing by itself).
/// </para>
/// <para>
/// An endpoint may raise its own cap with <c>RequestSizeLimitAttribute</c> metadata
/// (<c>.WithMetadata(new RequestSizeLimitAttribute(bytes))</c>): both mechanisms read that metadata first,
/// so the endpoint's own limit wins and the two can never disagree.
/// </para>
/// </remarks>
public sealed class RequestLimitsOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Kestrel:Limits";

    /// <summary>BD12's default cap: 1 MiB.</summary>
    public const long DefaultMaxRequestBodySize = 1024 * 1024;

    /// <summary>
    /// The upper bound this deployment accepts for the cap: 1 GiB. It is not a limit anything uses — it
    /// refuses a configuration typo (a stray zero) that would silently remove the cap altogether.
    /// </summary>
    public const long MaxAllowedRequestBodySize = 1024L * 1024 * 1024;

    /// <summary>The largest request body the host accepts, in bytes.</summary>
    public long MaxRequestBodySize { get; set; } = DefaultMaxRequestBodySize;
}
