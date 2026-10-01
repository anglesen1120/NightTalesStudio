using ErrorOr;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using NightTales.Api.Http;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.RateLimiting;

/// <summary>
/// The one rejection the limiter writes (09.3.5-T1 design, 09 README BD14/BD16, W8): a 429
/// <see cref="ProblemDetailsWriter"/> body with <c>code = rate_limited</c> and
/// <c>retryAfterSeconds</c>, the matching <c>Retry-After</c> header, one counted rejection and one log
/// line — never a raw address, a raw user id or a partition key that is either.
/// </summary>
/// <remarks>
/// <para>
/// The delegate is the limiter's <c>OnRejected</c>. It reads the policy that rejected the request from
/// the endpoint's own metadata, which is what makes the advertised delay the policy's window: the ASP.NET
/// Core callback exposes no reset instant (09 README W8), so a computed remainder could be zero or
/// negative while the client is still inside the window it must wait out.
/// </para>
/// <para>
/// Nothing here can throw into the middleware: a throw would surface as a 500 for a request the limiter
/// correctly refused, which is a worse answer than the 429. The body is written through the host's own
/// problem-details shape, so the response of a rejection is indistinguishable in form from every other
/// error of the Api and carries the request's trace id (D7).
/// </para>
/// <para>
/// The logged partition key is the key the limiter used, computed by the same function that produced it,
/// and it is a hash for an address and for a user id (BD16). The policy name is the only other field, so
/// a log line can never carry a caller's address, a user id or a ticket.
/// </para>
/// </remarks>
/// <param name="options">The configured limits and the advertised delay.</param>
/// <param name="metrics">The rejection counter of this host.</param>
/// <param name="ipResolver">The resolver the logged partition key is recomputed through.</param>
/// <param name="logger">The host's logger.</param>
public sealed partial class RateLimitRejection(
    RateLimitingOptions options,
    RateLimitMetrics metrics,
    IClientIpResolver ipResolver,
    ILogger<RateLimitRejection> logger)
{
    /// <summary>The client-safe title of the 429 body (09 README D7).</summary>
    public const string TooManyRequestsTitle = "Too many requests.";

    /// <summary>
    /// Writes the 429 of one rejected request; the limiter's <c>OnRejected</c> delegate
    /// (<c>Func&lt;OnRejectedContext, CancellationToken, ValueTask&gt;</c>).
    /// </summary>
    /// <param name="context">The rejection, whose lease the limiter already marked as not acquired.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    public ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (cancellationToken.IsCancellationRequested)
        {
            // The caller is gone: writing a body into a cancelled request would only throw inside the
            // middleware. The rejection is still counted and logged below, so an operator sees it.
            return ValueTask.CompletedTask;
        }

        var http = context.HttpContext;
        var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var partitionKey = policy is null ? null : RateLimitPartitionKeys.For(http, ipResolver, policy);

        metrics.RecordRejection(policy);
        LogRejected(policy ?? RateLimitMetrics.UnattributedPolicy, partitionKey ?? RateLimitMetrics.UnattributedPolicy);

        var error = Error.Failure(
            code: ErrorCodes.RateLimited,
            description: TooManyRequestsTitle,
            metadata: new Dictionary<string, object>
            {
                [ErrorMetadata.RetryAfterSeconds] = options.RetryAfterSeconds,
            });

        return new ValueTask(ProblemDetailsWriter.ToProblem([error], http).ExecuteAsync(http));
    }

    [LoggerMessage(
        EventId = 2601,
        Level = LogLevel.Warning,
        Message = "rate.limit.rejected: policy={Policy} partition={PartitionKey}")]
    private partial void LogRejected(string policy, string partitionKey);
}
