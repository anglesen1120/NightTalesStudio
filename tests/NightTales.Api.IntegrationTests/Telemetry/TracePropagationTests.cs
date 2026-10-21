using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Email;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.Infrastructure.Telemetry;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Telemetry;

/// <summary>
/// 09.9.3-T1 acceptance: one trace from the request through the outbox delivery to the Hangfire job it
/// enqueued (CR-1), and the hashed <c>userId</c> on a user-owned background log line (CR-4). Both run through
/// the production Worker graph with <c>AddNtsTelemetry</c> composed in.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Hangfire server")]
public sealed class TracePropagationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string RequestSourceName = "nts.test.request";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Request_outbox_delivery_and_hangfire_job_share_one_trace()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        using var requests = new ActivitySource(RequestSourceName);
        using var capture = new ActivityCapture();

        await using var harness = await EmailHarness.StartAsync(
            Database, new FakeClock(), ct, host => host.WithHangfireServer().WithTelemetry());

        ActivityTraceId requestTrace;
        ActivitySpanId requestSpan;
        using (var request = requests.StartActivity("request"))
        {
            request.ShouldNotBeNull();
            requestTrace = request.TraceId;
            requestSpan = request.SpanId;

            // The write that raises the domain event runs inside the "request": the outbox row stores its
            // traceparent (D11).
            await harness.QueueAsync(
                owner.Id,
                "password_changed",
                Language.Vi,
                EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
                ct);
        }

        // The delivery runs after the request ended, on the dispatcher's own thread of control; the job it
        // enqueues is executed by the real Hangfire server, a third execution context. Other tests' outbox
        // deliveries and jobs reach the same process-wide listener, so both spans are selected by the unique
        // parentage that the stored traceparent and the job parameters carry, not by operation name alone.
        (await harness.RunOutboxAsync(ct)).ShouldBeGreaterThan(0);

        var delivery = await capture.WhenStopped(
            activity => activity.OperationName == OutboxDispatcher.ActivityName
                && activity.ParentSpanId == requestSpan,
            ct).WaitAsync(Timeout, ct);
        var job = await capture.WhenStopped(
            activity => activity.Source.Name == JobTelemetryFilter.ActivitySourceName
                && activity.ParentSpanId == delivery.SpanId,
            ct).WaitAsync(Timeout, ct);

        delivery.TraceId.ShouldBe(requestTrace, "the outbox delivery is a child of the request that raised the event");
        job.TraceId.ShouldBe(requestTrace, "the job span joins the request's trace, not a new one");
        job.ParentSpanId.ShouldBe(delivery.SpanId, "the job is a child of the delivery that enqueued it");
    }

    [Fact]
    public async Task User_owned_job_log_line_carries_the_hashed_user_id()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        var sink = new CapturingSink();
        await using var harness = await EmailHarness.StartAsync(
            Database,
            new FakeClock(),
            ct,
            host => host.WithTelemetry().ConfigureServices(services => services.AddSingleton<ILogEventSink>(sink)));

        await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);
        (await harness.DeliverAsync(ct)).ShouldBe(1);

        var expected = new UserHashEnricher(
            new UserHashEnricherOptions(WorkerHost.TelemetrySalt), new HttpContextAccessor()).Hash(owner.Id.ToString("D"));
        var sent = sink.Events
            .Where(e => e.MessageTemplate.Text.StartsWith("email.sent", StringComparison.Ordinal))
            .ToList()
            .ShouldHaveSingleItem();

        sent.Properties.ShouldContainKey(UserHashEnricher.PropertyName);
        ((ScalarValue)sent.Properties[UserHashEnricher.PropertyName]).Value.ShouldBe(expected);
        foreach (var line in sink.Events)
        {
            if (line.Properties.TryGetValue(UserHashEnricher.PropertyName, out var value))
            {
                ((ScalarValue)value).Value.ShouldNotBe(owner.Id.ToString("D"), "userId is the salted hash, never the raw id");
            }
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }
    }
}
