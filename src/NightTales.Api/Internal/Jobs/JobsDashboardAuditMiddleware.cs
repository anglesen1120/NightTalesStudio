using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;

namespace NightTales.Api.Internal.Jobs;

/// <summary>
/// Audits the dashboard and holds the second CSRF gate of its POSTs (09.2.6-T2 Design/Step 3).
/// </summary>
/// <remarks>
/// <para>
/// It wraps Hangfire's own branch (it is registered before <c>UseHangfireDashboard</c>), so after the
/// dashboard ran it can read the session the authorization filter left on the request and write the two
/// rows of the contract: <c>internal.jobs_viewed</c>, once per dashboard session, and
/// <c>internal.jobs_action</c>, once per command POST — with the command and the ids it named. A GET is
/// only audited when the dashboard answered 200 (a refused or unrouted request accessed nothing).
/// </para>
/// <para>
/// The row is written by 09.3.6-T1's writer, in its own transaction on the request's scope, after the
/// response was produced: a dashboard action and its audit row cannot be one transaction (the action
/// commits inside Hangfire's own storage), so the row is best-effort and a refused commit is logged, not
/// retried into the operator's session.
/// </para>
/// <para>
/// The POST gate is the belt to Hangfire's own antiforgery braces: Hangfire 1.8.25 validates
/// <c>IAntiforgery</c> for every POST itself (its <c>AspNetCoreDashboardMiddleware</c> calls
/// <c>IsRequestValidAsync</c> and 403s on failure, and its context emits the
/// <c>csrf-token</c>/<c>csrf-header</c> meta tags its JavaScript echoes back), so a POST with a foreign
/// <c>Origin</c> is refused here — before that check — with this surface's 404 instead of its 403.
/// </para>
/// </remarks>
/// <param name="next">
/// The rest of the pipeline. It is a constructor parameter because <see cref="IApplicationBuilder"/> hands
/// conventional middleware its <see cref="RequestDelegate"/> that way (<c>UseMiddleware</c> rejects a
/// constructor with no parameter to receive it); nothing else of the pipeline is read here.
/// </param>
/// <param name="appOptions">The platform's public origin, one of the two origins a POST may name.</param>
/// <param name="cache">Where a session's view row is remembered, so the <c>/stats</c> poll cannot write one per tick.</param>
/// <param name="logger">Where a refused audit commit is reported.</param>
public sealed partial class JobsDashboardAuditMiddleware(
    RequestDelegate next,
    IOptions<AppOptions> appOptions,
    IMemoryCache cache,
    ILogger<JobsDashboardAuditMiddleware> logger)
{
    /// <summary>The audit action of a dashboard view (09.2.6-T2 Design).</summary>
    public const string ViewedAction = "internal.jobs_viewed";

    /// <summary>The audit action of a dashboard command (09.2.6-T2 Design).</summary>
    public const string ActionAction = "internal.jobs_action";

    /// <summary>How many ids of one batch command the payload records.</summary>
    private const int MaxIds = 20;

    /// <summary>The <c>jobs[]</c> form field every batch command names its jobs in (hangfire.js).</summary>
    private const string IdsFormField = "jobs[]";

    /// <summary>How many characters of the caller's <c>User-Agent</c> one row keeps.</summary>
    private const int UserAgentMaxLength = 512;

    /// <summary>The single-job command path prefix; its last segment is the job id.</summary>
    private const string SingleCommandPrefix = "/jobs/actions/";

    /// <summary>The web-serializer defaults of a hand-written <c>data</c> payload — camelCase keys, like the
    /// pipeline's projections and <c>SecurityAudit</c>'s rows. Built once (CA1869).
    /// </summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The middleware; the constructor's <c>next</c> reaches Hangfire's dashboard branch.</summary>
    /// <param name="context">The request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Path.StartsWithSegments(
                JobsDashboardAuthentication.DashboardPath,
                StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);

            return;
        }

        if (HttpMethods.IsPost(context.Request.Method) && !IsOriginAllowed(context.Request))
        {
            // Cross-origin: refused before the dashboard's antiforgery check, so the answer is this
            // surface's plain 404 and not Hangfire's 403 (it also never reaches the filter, so no row is
            // written for it: nothing was accessed).
            await JobsDashboardAuthentication.RefuseAsync(context, context.RequestAborted).ConfigureAwait(false);

            return;
        }

        await next(context).ConfigureAwait(false);

        // Set by the authorization filter, which is the only thing that authorizes a dashboard request:
        // its absence means the request was refused (or never reached the dashboard) and is not audited.
        if (context.Items[JobsDashboardAuthentication.SessionItemKey] is not JobsDashboardSession session)
        {
            return;
        }

        await AuditAsync(context, session).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a POST may proceed: an absent <c>Origin</c> is left to the antiforgery check (a
    /// non-browser client sends none), a single value must name this host's own origin — the dashboard is
    /// served by the Api itself, so that is the origin its own browser sends — or the SPA's configured
    /// <c>App:PublicOrigin</c>, which is the origin allowed to mint the cookie. A duplicated header is
    /// malformed and refused, like the auth routes' own origin filter (SEC 09.2.1-T2).
    /// </summary>
    private bool IsOriginAllowed(HttpRequest request)
    {
        var origins = request.Headers.Origin;

        if (origins.Count == 0)
        {
            return true;
        }

        if (origins.Count != 1)
        {
            return false;
        }

        var presented = origins.ToString();

        return IsSameOrigin(presented, $"{request.Scheme}://{request.Host}")
            || IsSameOrigin(presented, appOptions.Value.PublicOrigin);
    }

    /// <summary>Whether <paramref name="origin"/> names the same scheme and authority as <paramref name="expected"/>.</summary>
    private static bool IsSameOrigin(string origin, string expected)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var presented)
            || !Uri.TryCreate(expected, UriKind.Absolute, out var wanted))
        {
            return false;
        }

        return string.Equals(presented.Scheme, wanted.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(presented.Authority, wanted.Authority, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes the row the response deserves, if any.</summary>
    private async Task AuditAsync(HttpContext context, JobsDashboardSession session)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            if (context.Response.StatusCode == StatusCodes.Status200OK)
            {
                await AuditViewAsync(context, session).ConfigureAwait(false);
            }

            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method)
            || JobsDashboardAuthorizationFilter.IsReadOnlyPost(JobsDashboardAuthorizationFilter.RelativePath(context)))
        {
            return;
        }

        var (command, ids) = Describe(context);

        await WriteAsync(
            context,
            session,
            ActionAction,
            JsonSerializer.Serialize(
                new ActionData(command, ids, context.Response.StatusCode),
                PayloadOptions),
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <c>internal.jobs_viewed</c> once per dashboard session: the dashboard polls <c>/stats</c>
    /// every two seconds and reloads pages often, so one row per session (the cookie's <c>jti</c>) is the
    /// honest record of "this operator opened the dashboard". A restart loses the memory, which costs at
    /// most one extra row per session; a refused commit is not remembered, so the next request retries.
    /// </summary>
    private async Task AuditViewAsync(HttpContext context, JobsDashboardSession session)
    {
        var key = $"NightTales:InternalJobs:viewed:{session.SessionId}";

        if (cache.TryGetValue<bool>(key, out var alreadyWritten) && alreadyWritten)
        {
            return;
        }

        var written = await WriteAsync(
            context,
            session,
            ViewedAction,
            JsonSerializer.Serialize(new ViewedData(session.SessionId), PayloadOptions),
            context.RequestAborted).ConfigureAwait(false);

        if (written)
        {
            cache.Set(key, true, JobsDashboardAuthentication.Lifetime);
        }
    }

    /// <summary>The command and the ids one dashboard POST named.</summary>
    /// <remarks>
    /// Hangfire's two single-job command routes carry the id as the last path segment
    /// (<c>/jobs/actions/requeue/(?&lt;JobId&gt;.+)</c> and <c>…/delete/…</c>); every batch command
    /// (<c>…/requeue</c>, <c>…/delete</c>, <c>/recurring/remove</c>, <c>/recurring/trigger</c>) names its
    /// ids in the <c>jobs[]</c> form field the dispatcher already read, so reading it here costs no
    /// second parse of the body.
    /// </remarks>
    private static (string Command, IReadOnlyList<string> Ids) Describe(HttpContext context)
    {
        var path = JobsDashboardAuthorizationFilter.RelativePath(context).Value ?? "/";

        if (path.StartsWith(SingleCommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var idSeparator = path.LastIndexOf('/');

            if (idSeparator >= SingleCommandPrefix.Length)
            {
                return (path[..idSeparator], [path[(idSeparator + 1)..]]);
            }
        }

        return (path, FormIds(context.Request));
    }

    /// <summary>The <c>jobs[]</c> values of a batch command's form body, bounded.</summary>
    private static IReadOnlyList<string> FormIds(HttpRequest request)
    {
        // A command POST is always form-encoded (the antiforgery check and the batch dispatcher both
        // require it) — but a crafted request may not be, and reading Form on a non-form body throws.
        if (!request.HasFormContentType)
        {
            return [];
        }

        return request.Form.TryGetValue(IdsFormField, out var values)
            ? [.. values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Take(MaxIds)]
            : [];
    }

    /// <summary>Writes one audit row in its own transaction on the request's scope.</summary>
    /// <returns>Whether the row committed.</returns>
    private async Task<bool> WriteAsync(
        HttpContext context,
        JobsDashboardSession session,
        string action,
        string dataJson,
        CancellationToken ct)
    {
        var services = context.RequestServices;
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var writer = services.GetRequiredService<IAuditWriter>();
        var clock = services.GetRequiredService<IClock>();
        var requestInfo = services.GetRequiredService<IRequestInfoAccessor>().Current;

        await unitOfWork.BeginAsync(ct).ConfigureAwait(false);
        await writer.WriteAsync(
            new AuditEntry(
                ActorUserId: session.UserId,
                // An owner is staff on the platform's own surface, exactly as HttpCurrentUser reads their
                // role: the audit's actor type is "admin" for every owner and admin action (09 README D10).
                ActorType: ActorType.Admin,
                SubjectUserId: null,
                Action: action,
                Category: Category.Support,
                Visibility: Visibility.Internal,
                EntityType: null,
                EntityId: null,
                DataJson: dataJson,
                Ip: ParseIp(requestInfo.Ip),
                UserAgent: Truncate(requestInfo.UserAgent),
                ActorLabel: null,
                OccurredAt: clock.UtcNow),
            ct).ConfigureAwait(false);

        var committed = await unitOfWork.CommitAsync(ct).ConfigureAwait(false);

        if (committed.IsError)
        {
            LogAuditNotCommitted(action, session.UserId, committed.FirstError.Code);

            return false;
        }

        return true;
    }

    /// <summary>
    /// A refused audit commit (the row is lost, the dashboard request already answered): it is a
    /// <c>LogLevel.Error</c> so 09.9.3's pipeline can alert on it — an unaudited operator action is
    /// exactly the kind of gap an audit trail must never hide.
    /// </summary>
    [LoggerMessage(
        EventId = 2700,
        Level = LogLevel.Error,
        Message = "internal.jobs.audit.not-committed action={Action} actor={ActorUserId} code={Code}")]
    private partial void LogAuditNotCommitted(string action, Guid actorUserId, string code);

    /// <summary>The caller's address, or null when it is absent or unparsable — like the security rows.</summary>
    private static IPAddress? ParseIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out var parsed) ? parsed : null;

    /// <summary>
    /// The caller's <c>User-Agent</c>, bounded: the column is unbounded <c>text</c>, and a client header
    /// (Kestrel allows up to 32 KiB of them) must not be able to grow one row without limit. Same spirit
    /// as the refresh rows' bound, without reaching into that table's constant.
    /// </summary>
    private static string? Truncate(string? userAgent) =>
        userAgent is null or "" ? null
        : userAgent.Length <= UserAgentMaxLength ? userAgent
        : userAgent[..UserAgentMaxLength];

    /// <summary>The recorded shape of a view (09.2.6-T2 Design: <c>internal.jobs_viewed</c>).</summary>
    private sealed record ViewedData(string SessionId);

    /// <summary>
    /// The recorded shape of a command (09.2.6-T2 Design: <c>internal.jobs_action</c>, "with the job id"):
    /// the command the operator invoked, the ids it named, and the status the dashboard answered — so a
    /// refused attempt is as visible as an executed one.
    /// </summary>
    private sealed record ActionData(string Command, IReadOnlyList<string> Ids, int Status);
}

/// <summary>
/// The pipeline hook of the dashboard's audit middleware (09.2.6-T2 Steps 3/4): registered before
/// <c>UseHangfireDashboard</c>, so it wraps the dashboard's branch.
/// </summary>
public static class JobsDashboardAuditMiddlewareExtensions
{
    /// <summary>Adds <see cref="JobsDashboardAuditMiddleware"/> to the host's pipeline.</summary>
    /// <param name="app">The application builder.</param>
    public static IApplicationBuilder UseJobsDashboardAudit(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<JobsDashboardAuditMiddleware>();
    }
}
