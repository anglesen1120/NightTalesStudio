using System.Net;
using Microsoft.AspNetCore.Http;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Api.Security;

/// <summary>
/// The Api's <see cref="IRequestInfoAccessor"/> (09.3.1-T1): the caller's remote address and
/// <c>User-Agent</c> at the moment the handler asks, or two nulls outside a request.
/// </summary>
/// <remarks>
/// <para>
/// Both values are untrusted input and are only ever written to a row (a consent <c>ip</c>, an audit
/// row of 09.3.6-T1); they are never logged (09 README D17).
/// </para>
/// <para>
/// <c>RemoteIpAddress</c> is read as-is: forwarder headers are not trusted here — a deployment behind a
/// proxy configures the ASP.NET Core forwarded-headers middleware, which rewrites the connection
/// address, and trusting the headers directly would let a client choose the recorded address.
/// </para>
/// </remarks>
/// <param name="accessor">The host's HTTP context accessor.</param>
public sealed class HttpRequestInfoAccessor(IHttpContextAccessor accessor) : IRequestInfoAccessor
{
    /// <inheritdoc />
    public RequestInfo Current
    {
        get
        {
            var context = accessor.HttpContext;
            if (context is null)
            {
                return new RequestInfo(null, null);
            }

            IPAddress? address = context.Connection.RemoteIpAddress;

            return new RequestInfo(address?.ToString(), context.Request.Headers.UserAgent.ToString());
        }
    }
}
