using System.Diagnostics;
using Metrics = System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Outbox;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Outbox;

/// <summary>
/// The Worker's <c>OutboxDispatcher</c> over a real PostgreSQL 17 (09 README D11, 09.2.4 plan §9): the
/// claim, the lease, the per-message scope under <c>ISystemContext</c>, the backoff, the dead letter, the
/// trace continuation and the metric.
/// </summary>
/// <remarks>
/// The rows are written by SQL, not by the interceptor: it is <c>OutboxSaveChangesInterceptorTests</c>
/// (Infrastructure) that proves the write side, so these tests control the <c>type</c>, the payload, the
/// attempts and the <c>trace_parent</c> of the row they deliver. Delivery is driven by
/// <see cref="WorkerHost.RunOutboxOnceAsync"/> — the same <c>ProcessBatchAsync</c> the loop calls — with
/// <see cref="FakeClock"/> instead of sleeping.
/// </remarks>
public sealed class OutboxDispatcherTests(TestDatabase database) : IntegrationTest(database)
{
    private const string ProbeType = "probe_outbox_event.v1";
    private const string PlatformProbeType = "probe_outbox_platform_event.v1";

    [Fact]
    public async Task Outbox_Dispatch_CallsHandlersInRegistrationOrder()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services =>
            {
                services.AddOutboxHandler<OutboxProbeEvent, OutboxFirstProbeHandler>();
                services.AddOutboxHandler<OutboxProbeEvent, OutboxSecondProbeHandler>();
            },
            ct);

        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, user.Id, clock.UtcNow, ct: ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        state.Calls.Select(call => call.Handler).ShouldBe(["first", "second"]);
        state.CallsOf("first").ShouldHaveSingleItem().Attempt.ShouldBe(1);
        (await ReadAsync(messageId, ct)).ProcessedAt.ShouldNotBeNull("both handlers returned, so the row is delivered");
    }

    [Fact]
    public async Task Outbox_CrashAfterHandlerBeforeProcessed_HandlerRunsAgain_IdempotentEffectOnce()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        await Database.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS {OutboxCrashProbeHandler.EffectTable} (message_id uuid PRIMARY KEY)",
            ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxCrashProbeHandler>(),
            ct);

        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, user.Id, clock.UtcNow, ct: ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        var crashed = await ReadAsync(messageId, ct);
        crashed.ProcessedAt.ShouldBeNull("the handler died before the row was marked processed");
        crashed.Attempts.ShouldBe(1);
        crashed.NextAttemptAt.ShouldBe(clock.UtcNow.AddSeconds(2), "the failure backs off by 2^attempts seconds");
        EffectCount(messageId).ShouldBe(1, "the effect the handler committed before crashing survived");

        // The lease the claim stamped is over, so the row is claimed again and delivered a second time.
        clock.Advance(TimeSpan.FromMinutes(5));
        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        var delivered = await ReadAsync(messageId, ct);
        delivered.ProcessedAt.ShouldNotBeNull();
        state.Deliveries(messageId).ShouldBe(2, "delivery is at-least-once after a crash mid-message");
        EffectCount(messageId).ShouldBe(1, "the handler is idempotent, so its effect happened exactly once");
    }

    [Fact]
    public async Task Outbox_TwoDispatchers_NeverProcessSameRowConcurrently()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        const int messages = 200;
        const string label = "bulk";
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxCountingProbeHandler>(),
            ct);

        await InsertBulkAsync(label, messages, user.Id, clock.UtcNow, ct);
        var ids = BulkIds(label);
        ids.Count.ShouldBe(messages);

        // Two dispatchers over one table: SKIP LOCKED plus the lease the claim commits must make their
        // claims disjoint — one message, one delivery.
        var first = ActivatorUtilities.CreateInstance<OutboxDispatcher>(host.Services);
        var second = ActivatorUtilities.CreateInstance<OutboxDispatcher>(host.Services);
        await Task.WhenAll(DrainAsync(first, ct), DrainAsync(second, ct));

        foreach (var id in ids)
        {
            state.Deliveries(id).ShouldBe(1, $"message {id} was delivered exactly once");
        }

        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE payload->>'label' = '{label}' AND processed_at IS NULL")
            .ShouldBe(0, "both dispatchers drained the table");
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE payload->>'label' = '{label}' AND attempts > 0")
            .ShouldBe(0, "no delivery failed, so no message was delivered twice");
    }

    [Fact]
    public async Task Outbox_HandlerFailure_BacksOffExponentially()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxFailingProbeHandler>(),
            ct);

        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, user.Id, clock.UtcNow, ct: ct);

        foreach (var seconds in new[] { 2, 4, 8 })
        {
            (await host.RunOutboxOnceAsync(ct)).ShouldBe(1, "the row is due again after the previous backoff");

            var failed = await ReadAsync(messageId, ct);
            failed.ProcessedAt.ShouldBeNull();
            failed.NextAttemptAt.ShouldBe(clock.UtcNow.AddSeconds(seconds), "the backoff of D11 is 2^attempts seconds");

            // Move the clock onto the backoff: the next attempt is then due and the loop keeps going.
            clock.Set(failed.NextAttemptAt);
        }

        state.Deliveries(messageId).ShouldBe(3);

        // 2^9 = 512 s is above the cap, so a message that already failed eight times backs off 300 s.
        await Database.ExecuteAsync(
            $"UPDATE outbox_messages SET attempts = 8, next_attempt_at = '{clock.UtcNow:O}' WHERE id = '{messageId}'",
            ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        (await ReadAsync(messageId, ct)).NextAttemptAt
            .ShouldBe(clock.UtcNow.AddSeconds(OutboxDispatcher.MaxBackoffSeconds), "the backoff is capped at five minutes");
    }

    [Fact]
    public async Task Outbox_TenFailures_DeadLetters()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        using var metrics = new MetricsListener();

        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxFailingProbeHandler>(),
            ct);

        metrics.Watch(host.Services.GetRequiredService<OutboxMetrics>().Meter);

        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, user.Id, clock.UtcNow, ct: ct);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            (await host.RunOutboxOnceAsync(ct)).ShouldBe(1, $"attempt {attempt} claims the message");
            clock.Advance(TimeSpan.FromMinutes(6));
        }

        var dead = await ReadAsync(messageId, ct);
        dead.FailedAt.ShouldNotBeNull("the tenth failure makes the message a dead letter");
        dead.ProcessedAt.ShouldBeNull();
        dead.Attempts.ShouldBe(10);
        dead.Error.ShouldNotBeNull();
        dead.Error.ShouldContain(nameof(OutboxProbeFailureException));
        state.Deliveries(messageId).ShouldBe(10, "no handler runs after the dead letter");

        // A dead letter is never claimed again, however far the clock moves past its next_attempt_at.
        (await host.RunOutboxOnceAsync(ct)).ShouldBe(0);
        state.Deliveries(messageId).ShouldBe(10);
        metrics.DeadLetters.ShouldBe(1, "outbox_dead_letters_total counts the poison message");
    }

    /// <summary>
    /// An event name the registry does not know — a row written by a deployment that has not caught up, or
    /// one whose handler was dropped: no handler can ever run, so the row is a dead letter at once instead
    /// of blocking the batch.
    /// </summary>
    [Fact]
    public async Task Outbox_UnknownType_DeadLettersWithoutHandlers()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxCountingProbeHandler>(),
            ct);

        var messageId = Guid.CreateVersion7();
        await InsertAsync(messageId, "probe_outbox_event.v2", "{}", user.Id, clock.UtcNow, ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        var dead = await ReadAsync(messageId, ct);
        dead.FailedAt.ShouldNotBeNull("an unregistered type can never be delivered");
        dead.ProcessedAt.ShouldBeNull();
        dead.Attempts.ShouldBe(0, "no handler was ever tried, so no attempt was spent");
        dead.Error.ShouldNotBeNull();
        dead.Error.ShouldContain("probe_outbox_event.v2");
        state.Calls.ShouldBeEmpty("no handler of any type ran");
    }

    /// <summary>
    /// The lag the alert of 09 §8 reads is the age of the <em>oldest</em> due row, not of whichever row the
    /// claim happened to return first (<c>UPDATE … RETURNING</c> promises no order): the younger row is
    /// written first and the older one last, and the gauge still reports three minutes.
    /// </summary>
    [Fact]
    public async Task Outbox_Lag_ReportsTheOldestDueRow_WhateverOrderTheClaimReturns()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        using var metrics = new MetricsListener();
        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxCountingProbeHandler>(),
            ct);

        metrics.Watch(host.Services.GetRequiredService<OutboxMetrics>().Meter);

        var now = clock.UtcNow;
        await InsertProbeAsync(Guid.CreateVersion7(), user.Id, now - TimeSpan.FromSeconds(5), "younger", ct: ct);
        await InsertProbeAsync(Guid.CreateVersion7(), user.Id, now - TimeSpan.FromMinutes(3), "older", ct: ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(2, "both rows are due");

        metrics.ReadLagSeconds().ShouldBe(
            180d,
            "three minutes is the age of the oldest due row, whatever order the batch came back in");
    }

    /// <summary>
    /// The dispatcher-side half of the D10/D11 rule: a non-platform event stored without a user can never
    /// run, so the row is dead-lettered at once — and no handler of its type is even built, because a
    /// handler must never run unfiltered.
    /// </summary>
    [Fact]
    public async Task Outbox_NonPlatformEventWithoutUser_DeadLettersWithoutBuildingAHandler()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        using var metrics = new MetricsListener();

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxCountingProbeHandler>(),
            ct);

        metrics.Watch(host.Services.GetRequiredService<OutboxMetrics>().Meter);

        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, userId: null, clock.UtcNow, ct: ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        var dead = await ReadAsync(messageId, ct);
        dead.FailedAt.ShouldNotBeNull("a message with no user to run as is terminal, not retryable");
        dead.ProcessedAt.ShouldBeNull();
        dead.Attempts.ShouldBe(0, "the message never reached a handler, so no attempt was spent");
        dead.Error.ShouldNotBeNull();
        dead.Error.ShouldContain("needs a user_id");
        state.Calls.ShouldBeEmpty("a handler must never run unfiltered (09 README D10)");
        metrics.DeadLetters.ShouldBe(1, "the terminal failure is a dead letter too");
    }

    /// <summary>
    /// The attempt budget is configuration, not a constant: with <c>Outbox:MaxAttempts = 2</c> the first
    /// failure backs off inside the budget and the second is the one that dead-letters, keeping the count
    /// that exhausted it.
    /// </summary>
    [Fact]
    public async Task Outbox_ConfiguredMaxAttempts_DeadLettersOnTheLastAllowedAttempt()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxFailingProbeHandler>(),
            ct,
            maxAttempts: 2);

        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, user.Id, clock.UtcNow, ct: ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        var first = await ReadAsync(messageId, ct);
        first.ProcessedAt.ShouldBeNull();
        first.FailedAt.ShouldBeNull("one failure is still inside a budget of two");
        first.Attempts.ShouldBe(1);
        first.NextAttemptAt.ShouldBe(clock.UtcNow.AddSeconds(2), "the backoff of attempt one is 2^1 seconds");

        clock.Set(first.NextAttemptAt);
        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        var dead = await ReadAsync(messageId, ct);
        dead.FailedAt.ShouldNotBeNull("the second failure exhausts Outbox:MaxAttempts = 2");
        dead.ProcessedAt.ShouldBeNull();
        dead.Attempts.ShouldBe(2, "the dead letter keeps the count that exhausted the budget");
        dead.Error.ShouldNotBeNull();
        dead.Error.ShouldContain(nameof(OutboxProbeFailureException));
        state.Deliveries(messageId).ShouldBe(2);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(0, "a dead letter is never claimed again");
        state.Deliveries(messageId).ShouldBe(2);
    }

    /// <summary>
    /// The lease is what makes a crash recoverable without letting two dispatchers deliver the same row: a
    /// row a claim has already stamped (here four minutes of the five-minute default lease) is invisible to
    /// the claim, and it comes back the moment its lease is over.
    /// </summary>
    [Fact]
    public async Task Outbox_RowUnderItsLease_IsClaimedAgainOnlyAfterTheLease()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxCountingProbeHandler>(),
            ct);

        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, user.Id, clock.UtcNow, ct: ct);

        // What the claim of another Worker leaves behind: the row is still due, but the lease pushed
        // next_attempt_at four minutes out, inside the five-minute lease of the default options.
        var leasedUntil = clock.UtcNow.AddMinutes(4);
        await Database.ExecuteAsync(
            $"UPDATE outbox_messages SET next_attempt_at = '{leasedUntil:O}' WHERE id = '{messageId}'",
            ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(0, "a row another dispatcher claimed is not due yet");
        state.Deliveries(messageId).ShouldBe(0);

        clock.Set(leasedUntil);
        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1, "the row is due again the instant its lease is over");
        state.Deliveries(messageId).ShouldBe(1);
        (await ReadAsync(messageId, ct)).ProcessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Outbox_HandlerUnderUserA_CannotReadUserB()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        var owner = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var other = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var seriesOfOther = await new SeriesBuilder(Database, new TestCurrentUser { Id = other.Id })
            .CreateAsync(other.Id, ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxTenantProbeHandler>(),
            ct);

        state.ForeignSeriesId = seriesOfOther.Id;
        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, owner.Id, clock.UtcNow, ct: ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        state.CallsOf("tenant").ShouldHaveSingleItem().UserId
            .ShouldBe(owner.Id, "the delivery ran as the message's user, not anonymously");
        state.ForeignSeriesResult.ShouldBeNull("the tenant filter hides another user's series (09 README D10)");
        (await ReadAsync(messageId, ct)).ProcessedAt.ShouldNotBeNull("a handler that finds nothing is a success");
    }

    [Fact]
    public async Task Outbox_HandlerSpan_ContinuesCommandTrace()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbeEvent, OutboxTraceProbeHandler>(),
            ct);

        var traceParent = CreateTraceParent();
        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, user.Id, clock.UtcNow, traceParent: traceParent, ct: ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        var call = state.CallsOf("trace").ShouldHaveSingleItem();
        call.ActivityName.ShouldBe(OutboxDispatcher.ActivityName);
        call.ActivityParentId.ShouldBe(traceParent, "the delivery span is a child of the command that raised the event");
    }

    [Fact]
    public async Task Outbox_PlatformEvent_DispatchesWithoutAUser()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();

        await using var host = await OutboxHostAsync(
            clock,
            state,
            services => services.AddOutboxHandler<OutboxProbePlatformEvent, OutboxPlatformProbeHandler>(),
            ct);

        var messageId = Guid.CreateVersion7();
        await InsertAsync(
            messageId,
            PlatformProbeType,
            JsonSerializer.Serialize(new OutboxProbePlatformEvent(clock.UtcNow), OutboxJson.Options),
            userId: null,
            clock.UtcNow,
            ct);

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1);

        state.CallsOf("platform").ShouldHaveSingleItem().UserId.ShouldBeNull("a platform event has no user");
        (await ReadAsync(messageId, ct)).ProcessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Outbox_StartupClaim_DoesNotDeliverBeforeTheTestAsks()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var state = new OutboxProbeState();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        // The host is built here instead of through OutboxHostAsync, because the row must exist *before*
        // the start: that is the moment the Worker's loop used to claim once, and the harness stops it.
        await using var host = new WorkerHost(Database, clock);
        host.ConfigureServices(services =>
        {
            services.AddSingleton(state);
            services.AddSingleton(Database);
            services.AddOutboxHandler<OutboxProbeEvent, OutboxCountingProbeHandler>();
        });

        await Database.ExecuteAsync("DELETE FROM outbox_messages", ct);
        var messageId = Guid.CreateVersion7();
        await InsertProbeAsync(messageId, user.Id, clock.UtcNow, ct: ct);

        await host.StartAsync(ct);

        host.Services.GetServices<IHostedService>()
            .ShouldNotContain(service => service is OutboxDispatcher, "the harness drops the Worker's outbox loop before building the host");

        var untouched = await ReadAsync(messageId, ct);
        untouched.ProcessedAt.ShouldBeNull("nothing delivers an outbox row in the harness until a test asks");
        untouched.Attempts.ShouldBe(0);
        state.Calls.ShouldBeEmpty();

        (await host.RunOutboxOnceAsync(ct)).ShouldBe(1, "RunOutboxOnceAsync is the only delivery path");
        state.Deliveries(messageId).ShouldBe(1);
        (await ReadAsync(messageId, ct)).ProcessedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// A started host with the probe state, the class database and <paramref name="configure"/> applied
    /// (probe handlers, fakes), before it starts — the only moment <see cref="WorkerHost"/> allows it.
    /// </summary>
    /// <param name="clock">The clock the dispatcher resolves instead of the system clock.</param>
    /// <param name="state">The shared handler recorder of the test.</param>
    /// <param name="configure">The probe handlers and fakes this test registers.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <param name="maxAttempts">
    /// The attempt budget to set as <c>Outbox:MaxAttempts</c>, or <c>null</c> to leave the production
    /// default of 10 in place.
    /// </param>
    /// <remarks>
    /// The tests of this class share one database, so the outbox is emptied first: a row another test left
    /// behind would otherwise be due for this one and be claimed by its batch.
    /// </remarks>
    private async Task<WorkerHost> OutboxHostAsync(
        FakeClock clock,
        OutboxProbeState state,
        Action<IServiceCollection> configure,
        CancellationToken cancellationToken,
        int? maxAttempts = null)
    {
        await Database.ExecuteAsync("DELETE FROM outbox_messages", cancellationToken);

        var host = new WorkerHost(Database, clock);
        if (maxAttempts is { } attempts)
        {
            host.WithSetting(
                $"{OutboxDispatcherOptions.SectionName}:{nameof(OutboxDispatcherOptions.MaxAttempts)}",
                attempts.ToString(CultureInfo.InvariantCulture));
        }

        host.ConfigureServices(services =>
        {
            services.AddSingleton(state);
            services.AddSingleton(Database);
            configure(services);
        });

        await host.StartAsync(cancellationToken);

        return host;
    }

    /// <summary>Drains the table with one dispatcher: it stops when a claim finds nothing due.</summary>
    private static async Task DrainAsync(OutboxDispatcher dispatcher, CancellationToken cancellationToken)
    {
        while (await dispatcher.ProcessBatchAsync(cancellationToken) > 0)
        {
            // A full or partial batch means more rows were due; the claim of an empty batch ends the loop.
        }
    }

    /// <summary>
    /// A listener for this host's outbox instruments: the dead-letter counter (reported as it is written)
    /// and the lag gauge (read on demand, since a gauge is only evaluated when something reads it). It is
    /// created <em>before</em> the host so its instruments are published to it when the host builds its
    /// meter, and <see cref="Watch"/> pins it to that exact meter instance, so a parallel test class's
    /// dispatcher is never counted here.
    /// </summary>
    private sealed class MetricsListener : IDisposable
    {
        private readonly Metrics.MeterListener _listener;
        private Metrics.Meter? _meter;
        private long _deadLetters;
        private double _lagSeconds;

        public MetricsListener()
        {
            _listener = new Metrics.MeterListener
            {
                InstrumentPublished = (instrument, published) =>
                {
                    if (string.Equals(instrument.Meter.Name, OutboxMetrics.MeterName, StringComparison.Ordinal)
                        && (string.Equals(instrument.Name, OutboxMetrics.DeadLettersInstrumentName, StringComparison.Ordinal)
                            || string.Equals(instrument.Name, OutboxMetrics.LagInstrumentName, StringComparison.Ordinal)))
                    {
                        published.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (IsTheHosts(instrument)
                    && string.Equals(instrument.Name, OutboxMetrics.DeadLettersInstrumentName, StringComparison.Ordinal))
                {
                    Interlocked.Add(ref _deadLetters, value);
                }
            });

            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                if (IsTheHosts(instrument))
                {
                    Volatile.Write(ref _lagSeconds, value);
                }
            });

            _listener.Start();
        }

        /// <summary>Reports the instruments of <paramref name="meter"/> — the host's own meter.</summary>
        /// <param name="meter">The meter of the host under test.</param>
        public void Watch(Metrics.Meter meter) => _meter = meter;

        /// <summary>The dead letters the host reported since it started.</summary>
        public long DeadLetters => Volatile.Read(ref _deadLetters);

        /// <summary>Reads the lag gauge now: the age of the oldest row the last claim found due, in seconds.</summary>
        public double ReadLagSeconds()
        {
            _listener.RecordObservableInstruments();

            return Volatile.Read(ref _lagSeconds);
        }

        /// <inheritdoc />
        public void Dispose() => _listener.Dispose();

        private bool IsTheHosts(Metrics.Instrument instrument) => ReferenceEquals(instrument.Meter, _meter);
    }

    /// <summary>A W3C trace context of a "command" that has already finished, as <c>trace_parent</c> stores it.</summary>
    private static string CreateTraceParent()
    {
        using var activity = new Activity("probe.command");
        var started = activity.Start()
            ?? throw new InvalidOperationException("The probe command activity did not start.");

        return started.Id ?? throw new InvalidOperationException("The probe activity has no W3C id.");
    }

    private Task InsertProbeAsync(
        Guid id,
        Guid? userId,
        DateTimeOffset occurredAt,
        string label = "probe",
        string? traceParent = null,
        CancellationToken ct = default) =>
        InsertAsync(
            id,
            ProbeType,
            JsonSerializer.Serialize(new OutboxProbeEvent(id, label, occurredAt), OutboxJson.Options),
            userId,
            occurredAt,
            ct,
            traceParent: traceParent);

    /// <summary>
    /// Writes one <c>outbox_messages</c> row, so the test owns what the interceptor would have written:
    /// the type, the payload, the user, the attempts and the trace.
    /// </summary>
    private Task InsertAsync(
        Guid id,
        string type,
        string payload,
        Guid? userId,
        DateTimeOffset occurredAt,
        CancellationToken ct,
        string? traceParent = null,
        int attempts = 0) =>
        Database.ExecuteAsync(
            $"""
             INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts, trace_parent)
             VALUES ('{id}', '{type}', '{payload}', {(userId is null ? "NULL" : $"'{userId}'")}, '{occurredAt:O}', '{occurredAt:O}', {attempts}, {(traceParent is null ? "NULL" : $"'{traceParent}'")})
             """,
            ct);

    /// <summary>Writes <paramref name="count"/> due probe rows of one user in one statement.</summary>
    private Task InsertBulkAsync(string label, int count, Guid userId, DateTimeOffset occurredAt, CancellationToken ct) =>
        Database.ExecuteAsync(
            $"""
             INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts)
             SELECT gen_random_uuid(),
                    '{ProbeType}',
                    jsonb_build_object(
                        'aggregateId', gen_random_uuid()::text,
                        'label', '{label}',
                        'occurredAt', '{occurredAt:O}'),
                    '{userId}',
                    '{occurredAt:O}',
                    '{occurredAt:O}',
                    0
             FROM generate_series(1, {count})
             """,
            ct);

    private List<Guid> BulkIds(string label) =>
        [.. SqlProbe.QueryStrings(Database, $"SELECT id::text FROM outbox_messages WHERE payload->>'label' = '{label}'")
            .Select(Guid.Parse)];

    private async Task<OutboxMessage> ReadAsync(Guid id, CancellationToken ct)
    {
        await using var context = Database.CreateAppDbContext();

        return await context.OutboxMessages.AsNoTracking().SingleAsync(message => message.Id == id, ct);
    }

    private long EffectCount(Guid messageId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM {OutboxCrashProbeHandler.EffectTable} WHERE message_id = '{messageId}'");
}
