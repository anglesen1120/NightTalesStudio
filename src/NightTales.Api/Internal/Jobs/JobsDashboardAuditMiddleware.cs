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

    /// <summary>
    /// How many view gates guard the once-per-session dedup (F3). The pool is fixed, so the guard cannot
    /// grow with the number of dashboard sessions; a hash collision only serialises two unrelated
    /// sessions for the length of one insert.
    /// </summary>
    private const int ViewGateCount = 64;

    /// <summary>How many characters of the caller's <c>User-Agent</c> one row keeps.</summary>
    private const int UserAgentMaxLength = 512;

    /// <summary>The single-job command path prefix; its last segment is the job id.</summary>
    private const string SingleCommandPrefix = "/jobs/actions/";

    /// <summary>
    /// How long a command's audit may take once the dashboard answered (SEC-0926T2-01). It is bounded so
    /// a stuck database cannot hold the host's thread, and independent of the request, because Hangfire
    /// has already committed the retry/delete/trigger by the time the row is written: a client that
    /// disconnects must not be able to erase the record of its own command.
    /// </summary>
    private static readonly TimeSpan CommandAuditTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The web-serializer defaults of a hand-written <c>data</c> payload — camelCase keys, like the
    /// pipeline's projections and <c>SecurityAudit</c>'s rows. Built once (CA1869).
    /// </summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The per-session write gates of <see cref="AuditViewAsync"/>, one pool per host.</summary>
    private readonly SemaphoreSlim[] _viewGates = CreateViewGates();

    /// <summary>A fixed pool of one-at-a-time gates, so the dedup guard is bounded by construction.</summary>
    private static SemaphoreSlim[] CreateViewGates()
    {
        var gates = new SemaphoreSlim[ViewGateCount];

        for (var index = 0; index < gates.Length; index++)
        {
            gates[index] = new SemaphoreSlim(1, 1);
        }

        return gates;
    }

    /// <summary>The middleware; the constructor's <c>next</c> reaches Hangfire's dashboard branch.</summary>
    /// <remarks>
    /// The audit runs in a <c>finally</c>: a command that Hangfire already committed must be recorded
    /// even when the downstream throws while finishing the response (SEC-0926T2-01), and
    /// <see cref="AuditAsync"/> never propagates its own failure, so it cannot mask that exception.
    /// </remarks>
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

        var unhandled = false;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            // Remembered, not swallowed: the throw is re-raised and the audit runs below it.
            unhandled = true;

            throw;
        }
        finally
        {
            await AuditAsync(context, unhandled).ConfigureAwait(false);
        }
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

    /// <summary>Writes the row the response deserves, if any, and never lets its own failure escape.</summary>
    /// <param name="context">The request whose response was produced.</param>
    /// <param name="unhandled">Whether the dashboard branch threw after doing its work.</param>
    /// <remarks>
    /// The session is read from the item the authorization filter leaves, which is the only thing that
    /// authorizes a dashboard request: its absence means the request was refused (or never reached the
    /// dashboard) and is not audited. Everything below the dispatch is logged rather than propagated —
    /// the response has already been produced, and an audit that threw from <see cref="InvokeAsync"/>'s
    /// <c>finally</c> would hide the dashboard's own exception.
    /// </remarks>
    private async Task AuditAsync(HttpContext context, bool unhandled)
    {
        if (context.Items[JobsDashboardAuthentication.SessionItemKey] is not JobsDashboardSession session)
        {
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method))
        {
            // A refused or failed page accessed nothing, so only a real 200 is audited.
            if (!unhandled && context.Response.StatusCode == StatusCodes.Status200OK)
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

        await AuditCommandAsync(context, session, unhandled).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <c>internal.jobs_action</c>'s row for one command POST (09.2.6-T2 Design).
    /// </summary>
    /// <remarks>
    /// The write gets its own bounded token instead of <c>RequestAborted</c> (SEC-0926T2-01): Hangfire
    /// has already committed the retry, delete or trigger inside its own storage when this runs, so a
    /// client that disconnects — whether while the dashboard answered or while the row is written —
    /// must not be able to erase the record of the command it successfully issued.
    /// </remarks>
    private async Task AuditCommandAsync(HttpContext context, JobsDashboardSession session, bool unhandled)
    {
        using var timeout = new CancellationTokenSource(CommandAuditTimeout);

        try
        {
            var (command, ids, total, truncated) = Describe(context);

            // A branch that threw never produced this status: the exception handler outside this
            // middleware answers 500, and the row says what the operator's command really met.
            var status = unhandled ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;

            await WriteAsync(
                context,
                session,
                ActionAction,
                JsonSerializer.Serialize(new ActionData(command, ids, total, truncated, status), PayloadOptions),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogAuditFailed(exception, ActionAction, session.UserId);
        }
    }

    /// <summary>
    /// Writes <c>internal.jobs_viewed</c> once per dashboard session: the dashboard polls <c>/stats</c>
    /// every two seconds and reloads pages often, so one row per session (the cookie's <c>jti</c>) is the
    /// honest record of "this operator opened the dashboard". A restart loses the memory, which costs at
    /// most one extra row per session; a refused commit is not remembered, so the next request retries.
    /// </summary>
    /// <remarks>
    /// The check, the write and the marking are single-flight per session (F3): two tabs that open the
    /// dashboard at the same moment would otherwise both miss the cache and both commit a row, and the
    /// table has no unique constraint on the session. The gate is chosen from a fixed pool, so the guard
    /// cannot grow with the number of sessions, and the cache is re-checked inside it because the
    /// request that held the gate may already have written the row. The marking happens only after a
    /// commit, so a failed write is still retried by the next request.
    /// </remarks>
    private async Task AuditViewAsync(HttpContext context, JobsDashboardSession session)
    {
        var key = $"NightTales:InternalJobs:viewed:{session.SessionId}";

        if (IsViewRecorded(key))
        {
            return;
        }

        var gate = _viewGates[(uint)session.SessionId.GetHashCode() % (uint)_viewGates.Length];

        try
        {
            await gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller is gone before the row could even be attempted; the next request retries.
            LogAuditCancelled(ViewedAction, session.UserId);

            return;
        }

        try
        {
            if (IsViewRecorded(key))
            {
                return;
            }

            try
            {
                if (await WriteAsync(
                        context,
                        session,
                        ViewedAction,
                        JsonSerializer.Serialize(new ViewedData(session.SessionId), PayloadOptions),
                        context.RequestAborted).ConfigureAwait(false))
                {
                    cache.Set(key, true, JobsDashboardAuthentication.Lifetime);
                }
            }
            catch (OperationCanceledException)
            {
                LogAuditCancelled(ViewedAction, session.UserId);
            }
            catch (Exception exception)
            {
                LogAuditFailed(exception, ViewedAction, session.UserId);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether this session's view row is remembered as written.</summary>
    private bool IsViewRecorded(string key) =>
        cache.TryGetValue<bool>(key, out var alreadyWritten) && alreadyWritten;

    /// <summary>The command, the ids an audit payload records and what the request named.</summary>
    /// <returns>
    /// The relative command path, at most <see cref="MaxIds"/> ids, the total number of ids the request
    /// named, and whether the payload had to drop any (SEC-0926T2-02).
    /// </returns>
    /// <remarks>
    /// Hangfire's two single-job command routes carry the id as the last path segment
    /// (<c>/jobs/actions/requeue/(?&lt;JobId&gt;.+)</c> and <c>…/delete/…</c>); every batch command
    /// (<c>…/requeue</c>, <c>…/delete</c>, <c>/recurring/remove</c>, <c>/recurring/trigger</c>) names its
    /// ids in the <c>jobs[]</c> form field the dispatcher already read, so reading it here costs no
    /// second parse of the body.
    /// </remarks>
    private static (string Command, IReadOnlyList<string> Ids, int Total, bool Truncated) Describe(
        HttpContext context)
    {
        var path = JobsDashboardAuthorizationFilter.RelativePath(context).Value ?? "/";

        if (path.StartsWith(SingleCommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var idSeparator = path.LastIndexOf('/');

            if (idSeparator >= SingleCommandPrefix.Length)
            {
                return (path[..idSeparator], [path[(idSeparator + 1)..]], 1, false);
            }
        }

        var (ids, total) = FormIds(context.Request);

        return (path, ids, total, total > ids.Count);
    }

    /// <summary>
    /// The <c>jobs[]</c> values of a batch command's form body: the first <see cref="MaxIds"/> kept for
    /// the payload, and the whole count.
    /// </summary>
    /// <remarks>
    /// The cap bounds what one audit row carries, and the count travels with it so a truncated batch is
    /// still visible as one (SEC-0926T2-02); the caller keeps nothing else, because materialising every
    /// id of a crafted 1 MiB form would allocate for no reader. The ids are counted as they are read,
    /// so the cap applies to the payload and never to the command — Hangfire dispatches every id it was
    /// given, whatever this records.
    /// </remarks>
    private static (IReadOnlyList<string> Ids, int Total) FormIds(HttpRequest request)
    {
        // A command POST is always form-encoded (the antiforgery check and the batch dispatcher both
        // require it) — but a crafted request may not be, and reading Form on a non-form body throws.
        if (!request.HasFormContentType)
        {
            return ([], 0);
        }

        var named = request.Form[IdsFormField];
        var ids = new List<string>(Math.Min(named.Count, MaxIds));
        var total = 0;

        foreach (var value in named)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            total++;

            if (ids.Count < MaxIds)
            {
                ids.Add(value!);
            }
        }

        return (ids, total);
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

    /// <summary>
    /// An audit that threw before it could commit (a database failure, the command audit's own timeout):
    /// like a refused commit it is a <c>LogLevel.Error</c>, and it carries no payload — the action and
    /// the actor are enough for 09.9.3's pipeline, and the row's data never reaches a log line.
    /// </summary>
    [LoggerMessage(
        EventId = 2701,
        Level = LogLevel.Error,
        Message = "internal.jobs.audit.failed action={Action} actor={ActorUserId}")]
    private partial void LogAuditFailed(Exception exception, string action, Guid actorUserId);

    /// <summary>
    /// A view row that was not attempted because the caller left while it waited for its turn. The next
    /// request retries, so this is not an incident; it is logged at <c>LogLevel.Information</c> for the
    /// operator who wonders why a session's page is missing from the trail.
    /// </summary>
    [LoggerMessage(
        EventId = 2702,
        Level = LogLevel.Information,
        Message = "internal.jobs.audit.cancelled action={Action} actor={ActorUserId}")]
    private partial void LogAuditCancelled(string action, Guid actorUserId);

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
    /// <param name="Command">The dashboard-relative command path.</param>
    /// <param name="Ids">At most <see cref="MaxIds"/> of the ids the command named.</param>
    /// <param name="Total">How many ids the command named, <paramref name="Ids"/> or more.</param>
    /// <param name="Truncated">Whether the command named more ids than the payload kept (SEC-0926T2-02).</param>
    /// <param name="Status">The HTTP status the dashboard answered.</param>
    private sealed record ActionData(
        string Command,
        IReadOnlyList<string> Ids,
        int Total,
        bool Truncated,
        int Status);
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
