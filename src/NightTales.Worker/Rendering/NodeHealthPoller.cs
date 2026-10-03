using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.Worker.Rendering;

/// <summary>
/// The Worker's render-node health poller (11 README D13, F10/F12, plan §7): one
/// <see cref="BackgroundService"/> that, every <c>RenderNodes:HealthPollSeconds</c>, pulls
/// <c>GET /v1/health</c> from every registered node through the pinned-TLS factory, stores the heartbeat and
/// the in-memory snapshot, and moves the node between <c>active</c> and <c>down</c> under hysteresis.
/// </summary>
/// <remarks>
/// <para>
/// It is a <see cref="BackgroundService"/> and not a Hangfire recurring job because a sub-minute cron is not
/// reliable (F10/P9, 09 README D11); the loop is single-instance per database through
/// <see cref="NodeHealthPollerLock"/> (Q5), so a second Worker during a rolling deploy only logs.
/// </para>
/// <para>
/// A <b>valid answer</b> is HTTP 200 <em>or</em> 503 with a body no larger than <see cref="MaxBodyBytes"/>
/// that parses and validates as <c>render-node-health.v1</c> — the node answered; a connect error, a pin
/// mismatch, a pull deadline, a 401, an oversized body or an unparseable/invalid one is no answer. A 401 is
/// still read: when its body parses, its reasons (for example <c>mpt_unauthorized</c>) are counted instead
/// of the local <c>node_unreachable</c>. Two consecutive valid
/// answers revive a <c>down</c> node (<see cref="GoodPullsForActive"/>);
/// <c>RenderNodes:DownAfterSeconds</c> without one sends an <c>active</c> node <c>down</c>. <c>draining</c>
/// is operator-only and health never ends it (D12).
/// </para>
/// <para>
/// Nothing about a node body reaches a log: an unparseable body logs the node's name and the exception's
/// class, a rejection logs the status code, and the adapter key never appears anywhere (11 README S4).
/// </para>
/// </remarks>
/// <param name="scopeFactory">Creates the per-tick scope the node reads and writes run in (09 README D11).</param>
/// <param name="options">The bound <c>RenderNodes</c> section: cadence, hysteresis and the drift threshold.</param>
/// <param name="connectionFactory">The only producer of node clients (F13, S3).</param>
/// <param name="cache">The in-memory health cache 11.3.3-T2's selector and gauges read (Q1).</param>
/// <param name="metrics">The node instruments recorded on every tick (D13).</param>
/// <param name="clock">The Worker's clock: the hysteresis uses UTC from here only, never a node timestamp (09 D3).</param>
/// <param name="pollLock">The advisory-lock single-instance guard (Q5).</param>
/// <param name="logger">Tick, lock, drift and per-pull failures.</param>
public sealed partial class NodeHealthPoller(
    IServiceScopeFactory scopeFactory,
    IOptions<RenderNodesOptions> options,
    IRenderNodeConnectionFactory connectionFactory,
    INodeHealthCache cache,
    NodeMetrics metrics,
    IClock clock,
    NodeHealthPollerLock pollLock,
    ILogger<NodeHealthPoller> logger) : BackgroundService
{
    /// <summary>Consecutive valid answers that revive a <c>down</c> node (11 README F12: exactly two).</summary>
    public const int GoodPullsForActive = 2;

    /// <summary>The parse-error label of a 200/503 with an empty body, logged instead of the body (S4).</summary>
    private const string MissingBodyError = "empty-body";

    /// <summary>The parse-error label of a body that refused to be read inside the cap, logged instead of it (S4).</summary>
    private const string BodyTooLargeError = "body-too-large";

    /// <summary>The parse-error label of a well-JSON body whose known fields are invalid (null/blank) (S4).</summary>
    private const string InvalidFieldsError = "invalid-fields";

    /// <summary>
    /// The health body cap (64 KiB): the contract shape is a few hundred bytes, so a larger answer —
    /// or a chunked body that never ends — is a compromised node denying the Worker memory (S4).
    /// </summary>
    private const int MaxBodyBytes = 64 * 1024;

    private readonly ConcurrentDictionary<string, int> _goodPulls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, int> _driftRuns = new();
    private readonly ConcurrentDictionary<string, NodeClient> _clients = new(StringComparer.Ordinal);
    private DateTimeOffset? _firstTickAt;
    private bool _lockNotHeldLogged;

    /// <summary>
    /// The loop: one <see cref="TickAsync"/> per interval. Every failure is caught and logged, so a database
    /// outage or a bad node never stops the Worker's outbox or Hangfire (plan §"Failure handling").
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.HealthPollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(exception);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One tick: take the advisory lock, then poll. The lock gates only the registry writes (heartbeat, state
    /// transitions, drift alert): an instance that does not hold it still pulls every node into its own
    /// process-local <see cref="INodeHealthCache"/> — the selector of that Worker reads nothing else, so a
    /// non-leader that skipped the pull would refuse every dispatch (11 README F10). Exposed because the
    /// harness drives a tick directly instead of waiting for a 30-second timer
    /// (<c>OutboxDispatcher.ProcessBatchAsync</c> is public for the same reason).
    /// </summary>
    /// <param name="cancellationToken">The tick's cancellation token.</param>
    /// <returns>
    /// <c>true</c> when this instance held the lock and polled as the leader; <c>false</c> when another
    /// instance does (this one only refreshed its cache), or when the lock session was lost and just
    /// re-taken — a tick decided across a lost lock is skipped.
    /// </returns>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var attempt = await pollLock.TryAcquireAsync(cancellationToken);

        // A lock whose session died between ticks is re-taken but this tick is skipped: another Worker may
        // have held the lock and polled during the gap, so decisions based on the pre-loss view stay off
        // the registry (plan §"Failure handling": a lost lock never flips a node state).
        if (attempt == NodeHealthPollerLock.LockAttempt.Recovered)
        {
            LogLockRecovered();

            return false;
        }

        if (attempt == NodeHealthPollerLock.LockAttempt.HeldElsewhere)
        {
            if (!_lockNotHeldLogged)
            {
                _lockNotHeldLogged = true;
                LogLockNotHeld();
            }

            await PollAsync(leader: false, cancellationToken);

            return false;
        }

        _lockNotHeldLogged = false;
        await PollAsync(leader: true, cancellationToken);

        return true;
    }

    /// <summary>
    /// Pulls every registered node once and applies the outcome as the leader: heartbeat, cache, hysteresis
    /// and metrics. Exposed for the harness's deterministic tick (<see cref="TickAsync"/> calls it).
    /// </summary>
    /// <param name="cancellationToken">The tick's cancellation token.</param>
    public Task PollOnceAsync(CancellationToken cancellationToken) => PollAsync(leader: true, cancellationToken);

    /// <summary>
    /// The pull. A non-leader refreshes its own cache and gauges (<c>render_node_up</c>, free slots) but
    /// writes nothing to the registry and keeps no hysteresis or drift state: those belong to the lock holder.
    /// </summary>
    private async Task PollAsync(bool leader, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.UtcNow;
        if (leader)
        {
            _firstTickAt ??= now;
        }

        using var scope = scopeFactory.CreateScope();

        var nodes = await scope.ServiceProvider.GetRequiredService<INodeRegistry>().AllAsync(cancellationToken);
        if (nodes.Count == 0)
        {
            return;
        }

        var pulls = await PullAllAsync(nodes, cancellationToken);
        var inFlight = await scope.ServiceProvider.GetRequiredService<INodeLoadReader>()
            .InFlightByNodeAsync(cancellationToken);

        var decisions = new List<TickDecision>(nodes.Count);

        foreach (var node in nodes)
        {
            var outcome = pulls[node.Name];
            var snapshot = outcome.Snapshot;
            RecordHealth(node, outcome);
            metrics.RecordLoad(node.Name, node.Capacity, inFlight.GetValueOrDefault(node.Id), node.State == WorkerNodeState.Active);

            if (!leader)
            {
                // The cache, render_node_up and the load gauge above are this process's own view; drift
                // runs, revival streaks and every registry decision belong to the lock holder alone.
                continue;
            }

            if (snapshot is { } answered)
            {
                RecordDrift(node, answered, inFlight, settings);
            }
            else
            {
                // A no-answer pull is not a clean drift pull either: the streak is "consecutive mismatches",
                // so silence resets it like a matching report does (plan §7, DriftAlertPulls).
                _driftRuns[node.Id] = 0;
            }

            if (node.State == WorkerNodeState.Draining)
            {
                // Health never ends a drain (D12): a valid answer still heartbeats, the state stays.
                if (snapshot is not null)
                {
                    decisions.Add(new TickDecision(node.Id, node.State, Heartbeat: true, Target: null));
                }
                else
                {
                    // A drain interrupts the revival streak: only answers that are consecutive *after* the
                    // node is back in service count, so a silent drain tick clears it (F12's "exactly two").
                    _goodPulls[node.Name] = 0;
                }

                continue;
            }

            if (snapshot is not null)
            {
                var streak = _goodPulls.AddOrUpdate(node.Name, 1, (_, current) => current + 1);
                var revive = node.State == WorkerNodeState.Down && streak >= GoodPullsForActive;
                decisions.Add(new TickDecision(node.Id, node.State, Heartbeat: true, Target: revive ? WorkerNodeState.Active : null));

                continue;
            }

            _goodPulls[node.Name] = 0;

            // A node that has never answered counts from the poller's first tick: "no valid answer for
            // DownAfterSeconds" is the rule, and a seeded Active row must eventually leave Active.
            var reference = node.LastHeartbeatAt ?? _firstTickAt!.Value;
            if (node.State == WorkerNodeState.Active
                && now - reference >= TimeSpan.FromSeconds(settings.DownAfterSeconds))
            {
                decisions.Add(new TickDecision(node.Id, node.State, Heartbeat: false, Target: WorkerNodeState.Down));
            }
        }

        if (decisions.Count == 0)
        {
            return;
        }

        await ApplyAsync(scope, decisions, now, cancellationToken);
    }

    /// <summary>Writes one UPDATE per decided node and commits the tick's single <c>SaveChanges</c>.</summary>
    private static async Task ApplyAsync(
        IServiceScope scope,
        IReadOnlyList<TickDecision> decisions,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ids = decisions.Select(decision => decision.Id).ToArray();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        // One query for the rows this tick touches; the read above was the no-tracking registry view.
        var tracked = await db.WorkerNodes
            .Where(node => ids.Contains(node.Id))
            .ToListAsync(cancellationToken);
        var byId = tracked.ToDictionary(node => node.Id);

        foreach (var decision in decisions)
        {
            if (!byId.TryGetValue(decision.Id, out var row))
            {
                // The row disappeared between the read and the write (an operator deleted it by hand);
                // the next tick reads the registry again.
                continue;
            }

            if (decision.Heartbeat)
            {
                row.RecordHeartbeat(now);
            }

            // The transition is only applied while the row still holds the state the decision assumed: an
            // operator's `nodes set-state` landing between the registry read and this write must win over
            // health, because health never ends a drain (D12).
            if (decision.Target is { } target && row.State == decision.Assumed)
            {
                if (target == WorkerNodeState.Active)
                {
                    row.MarkActive(now);
                }
                else
                {
                    row.MarkDown(now);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Polls every node with bounded concurrency (D13: at most <see cref="RenderNodesOptions.MaxConcurrentPulls"/>
    /// at once, 5 seconds each), so one dead node cannot push the tick past its interval.
    /// </summary>
    private async Task<Dictionary<string, PullOutcome>> PullAllAsync(
        IReadOnlyList<WorkerNode> nodes,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<string, PullOutcome>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(RenderNodesOptions.MaxConcurrentPulls);

        var pulls = nodes.Select(async node =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[node.Name] = await PullAsync(node, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(pulls);

        return new Dictionary<string, PullOutcome>(results, StringComparer.Ordinal);
    }

    /// <summary>
    /// One node's pull. Never throws for a node-side failure — the caller treats <see cref="PullOutcome"/>.Snapshot
    /// as <c>null</c> for "no valid answer" and the loop stays alive (plan §"Failure handling").
    /// </summary>
    private async Task<PullOutcome> PullAsync(WorkerNode node, CancellationToken cancellationToken)
    {
        // A pull owns its own deadline: the client's timeout ends at the headers (ResponseHeadersRead), so
        // the send and the body read share one linked token that dies at PullTimeout regardless of the
        // caller's lifetime.
        using var pullDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pullDeadline.CancelAfter(RenderNodesOptions.PullTimeout);
        var pull = pullDeadline.Token;

        try
        {
            var client = ClientFor(node);

            using var response = await client.GetAsync(
                RenderNodeRoutes.Health,
                HttpCompletionOption.ResponseHeadersRead,
                pull);

            var status = response.StatusCode;

            // 200 is "ok" and 503 is "the node answered but is not ok" — both are answers (D13). A 401 (a
            // wrong or rotated key) is *not* an answer: it must not heartbeat or revive a node, but its body
            // is still read for the `mpt_unauthorized` reason (task §"Failure handling"). Anything else (a
            // proxy error page) is no answer and its body is never read.
            var httpOk = status == HttpStatusCode.OK;
            var isAnswer = httpOk || status == HttpStatusCode.ServiceUnavailable;
            var isUnauthorized = status == HttpStatusCode.Unauthorized;

            if (!isAnswer && !isUnauthorized)
            {
                LogPullRejected(node.Name, (int)status);

                return PullOutcome.NoAnswer();
            }

            // The body is read as a bounded string (MaxBodyBytes): ReadFromJsonAsync would report its own
            // failure shape, the tolerant reader must be the only one (11 README D4), and a compromised
            // node must not buffer an unbounded body in the Worker's memory.
            var body = await ReadBodyAsync(node, response, pull);
            if (body is null || !TryReadHealth(node, body, out var health))
            {
                return PullOutcome.NoAnswer();
            }

            return isUnauthorized
                ? PullOutcome.Rejected(health.Reasons)
                : PullOutcome.Answer(NodeHealthSnapshot.From(node.Name, health, clock.UtcNow, httpOk));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Connect error, pin mismatch, pull deadline (the linked token expired), a rotated key: the
            // node's name and the failure's class only. An exception message can echo the request's URL,
            // which is never logged (11 README S4).
            LogPullFailed(node.Name, exception.GetType().Name);

            return PullOutcome.NoAnswer();
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxBodyBytes"/> of the body. A declared length over the cap is refused
    /// before the body is touched; an uncounted (chunked) stream is read only until the cap overflows.
    /// Returns <c>null</c> — logged as <see cref="BodyTooLargeError"/>, never the body — when the answer is
    /// bigger than the cap.
    /// </summary>
    private async Task<string?> ReadBodyAsync(
        WorkerNode node,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxBodyBytes)
        {
            LogUnparseableBody(node.Name, BodyTooLargeError);

            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // One byte of headroom decides "exactly the cap" apart from "one byte over": the buffer is sized to
        // the cap plus one, and reaching it already means the answer is rejected — the rest is never read.
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxBodyBytes)
            {
                LogUnparseableBody(node.Name, BodyTooLargeError);

                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// The semantic half of the tolerant reader (D4): <c>render-node-health.v1</c> may not carry a null or
    /// blank schema/status/reason, and every required reference block must be present. Unknown members stay
    /// ignorable; the check only refuses invalid known fields, which would otherwise let one node's body
    /// break the whole tick's heartbeat and down decisions.
    /// </summary>
    private static bool IsWellFormed(RenderNodeHealthV1 health) =>
        !string.IsNullOrWhiteSpace(health.Schema)
        && !string.IsNullOrWhiteSpace(health.Status)
        && health.Reasons is { } reasons && reasons.All(reason => !string.IsNullOrWhiteSpace(reason))
        && health.Capacity is not null
        && health.Mpt is not null
        && health.ImageRelay is not null
        && health.Assets is not null
        && health.Disk is not null
        && health.Gpu is not null
        && health.Adapter is not null;

    /// <summary>
    /// Deserialises a health body with the tolerant reader (D4). A parse failure, an empty body, or an
    /// invalid known field (a null/blank required value) logs the node's name and the error class — never
    /// the body (S4) — and reports <c>false</c>.
    /// </summary>
    private bool TryReadHealth(WorkerNode node, string body, out RenderNodeHealthV1 health)
    {
        health = null!;
        try
        {
            var parsed = JsonSerializer.Deserialize<RenderNodeHealthV1>(body, RenderNodeJson.Tolerant);
            if (parsed is null)
            {
                LogUnparseableBody(node.Name, MissingBodyError);

                return false;
            }
            if (!IsWellFormed(parsed))
            {
                // Every required block carries [JsonRequired], so an absent member never reaches here; a
                // *present but null* block or a null/blank schema, status or reason does, and such a body
                // must not drive a snapshot or a reason counter (the counters throw on blank values).
                LogUnparseableBody(node.Name, InvalidFieldsError);

                return false;
            }

            health = parsed;

            return true;
        }
        catch (JsonException exception)
        {
            LogUnparseableBody(node.Name, exception.GetType().Name);

            return false;
        }
    }

    /// <summary>
    /// The node's client: created once through the factory and pooled for the poller's lifetime (Q5). A node
    /// whose configuration the seeder rewrote gets a fresh client on the next tick; a running Worker is not
    /// expected to see that, because the seeder runs at start-up only (plan §"Failure handling").
    /// </summary>
    private HttpClient ClientFor(WorkerNode node)
    {
        if (_clients.TryGetValue(node.Name, out var cached) && cached.BaseUrl == node.BaseUrl)
        {
            return cached.Client;
        }

        var client = connectionFactory.Create(node);

        if (_clients.TryRemove(node.Name, out var replaced))
        {
            replaced.Client.Dispose();
        }

        _clients[node.Name] = new NodeClient(node.BaseUrl, client);

        return client;
    }

    /// <summary>Updates the cache and emits the tick's <c>render_node_up</c> and reason counters (D13).</summary>
    private void RecordHealth(WorkerNode node, PullOutcome outcome)
    {
        var snapshot = outcome.Snapshot;
        metrics.RecordUp(node.Name, snapshot is not null);

        if (snapshot is null)
        {
            // A rejected but parseable body (a 401) carries the node's own reasons; every other no-answer
            // is the local `node_unreachable` (task §"Failure handling").
            if (outcome.RejectedReasons.Count == 0)
            {
                metrics.RecordHealthReason(node.Name, LocalNodeHealthReasons.NodeUnreachable);
            }
            else
            {
                foreach (var reason in outcome.RejectedReasons)
                {
                    metrics.RecordHealthReason(node.Name, reason);
                }
            }

            return;
        }

        cache.Set(snapshot);

        foreach (var reason in snapshot.Reasons)
        {
            metrics.RecordHealthReason(node.Name, reason);
        }
    }

    /// <summary>
    /// UC9's drift signal: the reported <c>capacity.active_tasks</c> against the derived count, for
    /// <c>RenderNodes:DriftAlertPulls</c> consecutive ticks. It logs and counts once per run and never changes
    /// a state.
    /// </summary>
    private void RecordDrift(
        WorkerNode node,
        NodeHealthSnapshot snapshot,
        IReadOnlyDictionary<Guid, int> inFlight,
        RenderNodesOptions settings)
    {
        var derived = inFlight.TryGetValue(node.Id, out var count) ? count : 0;

        if (derived == snapshot.ActiveTasks)
        {
            _driftRuns[node.Id] = 0;

            return;
        }

        var run = _driftRuns.AddOrUpdate(node.Id, 1, (_, current) => current + 1);
        if (run == settings.DriftAlertPulls)
        {
            LogDrift(node.Name, derived, snapshot.ActiveTasks);
            metrics.RecordActiveTasksDrift(node.Name);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Client.Dispose();
        }

        _clients.Clear();
        base.Dispose();
    }

    [LoggerMessage(EventId = 2400, Level = LogLevel.Warning, Message = "render-node.poller.lock-not-held")]
    private partial void LogLockNotHeld();

    [LoggerMessage(EventId = 2401, Level = LogLevel.Error, Message = "render-node.poller.tick-failed")]
    private partial void LogTickFailed(Exception exception);

    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Warning,
        Message = "render-node.pull.rejected node={Node} status={Status}")]
    private partial void LogPullRejected(string node, int status);

    [LoggerMessage(
        EventId = 2403,
        Level = LogLevel.Warning,
        Message = "render-node.pull.unparseable node={Node} error={Error}")]
    private partial void LogUnparseableBody(string node, string error);

    [LoggerMessage(
        EventId = 2404,
        Level = LogLevel.Warning,
        Message = "render-node.pull.failed node={Node} error={Error}")]
    private partial void LogPullFailed(string node, string error);

    [LoggerMessage(
        EventId = 2405,
        Level = LogLevel.Warning,
        Message = "render-node.drift node={Node} derived={Derived} reported={Reported}")]
    private partial void LogDrift(string node, int derived, int reported);

    [LoggerMessage(
        EventId = 2406,
        Level = LogLevel.Warning,
        Message = "render-node.poller.lock-recovered")]
    private partial void LogLockRecovered();

    /// <summary>A node's pooled client and the base URL it was built for.</summary>
    private sealed record NodeClient(Uri BaseUrl, HttpClient Client);

    /// <summary>
    /// What one pull produced: the valid answer to cache and heartbeat, or — for a rejected body that still
    /// parsed (a 401) — only its reasons, which are counted while the pull stays "no answer" (task §"Failure
    /// handling").
    /// </summary>
    private sealed record PullOutcome(NodeHealthSnapshot? Snapshot, IReadOnlyList<string> RejectedReasons)
    {
        /// <summary>A connect error, a timeout, a pin mismatch or an unparseable body: no answer, no reason.</summary>
        public static PullOutcome NoAnswer() => new(null, []);

        /// <summary>A 401 whose body parsed: no answer, but its reasons are counted.</summary>
        public static PullOutcome Rejected(IReadOnlyList<string> reasons) => new(null, reasons);

        /// <summary>An answer (200, or 503 with a parseable body) worth caching and heartbeating.</summary>
        public static PullOutcome Answer(NodeHealthSnapshot snapshot) => new(snapshot, []);
    }

    /// <summary>
    /// What one tick decided for one node; <c>Target</c> is <c>null</c> when only the heartbeat moves.
    /// <c>Assumed</c> is the registry's state when the decision was made: the write applies
    /// <c>Target</c> only while the row still holds it, so an operator drain can never be overwritten.
    /// </summary>
    private sealed record TickDecision(Guid Id, WorkerNodeState Assumed, bool Heartbeat, WorkerNodeState? Target);
}
