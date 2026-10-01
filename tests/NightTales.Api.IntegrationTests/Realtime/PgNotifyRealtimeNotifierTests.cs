using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// <see cref="PgNotifyRealtimeNotifier"/> against a real PostgreSQL 17: the body cap has to sit one byte
/// below PostgreSQL's own payload limit, because the database refuses a payload of exactly 8000 bytes
/// (<c>22023 payload string too long</c>) and that failure would roll the producer's command back
/// (09.8.1-T1 failure table: an over-sized body is dropped and counted, never raised).
/// </summary>
public sealed class PgNotifyRealtimeNotifierTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly Guid UserId = Guid.Parse("018f2c58-5555-7a5b-8f7a-1c2d3e4f5a65");

    /// <summary>PostgreSQL 17's own NOTIFY payload limit, exclusive ("must be shorter than 8000 bytes").</summary>
    private const int PostgresPayloadLimitBytes = 8_000;

    [Fact]
    public async Task Postgres_Accepts_A_Payload_One_Byte_Under_The_Configured_Cap_And_Refuses_The_Cap_Itself()
    {
        var ct = TestContext.Current.CancellationToken;

        // The fact the cap is measured against: PostgreSQL's limit is exclusive.
        await Should.NotThrowAsync(() => Database.ExecuteAsync(
            $"SELECT pg_notify('realtime', repeat('x', {PostgresPayloadLimitBytes - 1}))",
            ct));

        var tooLong = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            $"SELECT pg_notify('realtime', repeat('x', {PostgresPayloadLimitBytes}))",
            ct));

        tooLong.SqlState.ShouldBe("22023", "a body of exactly the cap is refused by PostgreSQL");
    }

    [Fact]
    public async Task Notify_A_Body_At_The_Configured_Cap_Is_Dropped_Instead_Of_Raised()
    {
        var ct = TestContext.Current.CancellationToken;
        using var metrics = new RealtimeMetrics();
        using var dropped = new DropCounter(metrics);
        await using var context = Database.CreateAppDbContext();
        var notifier = new PgNotifyRealtimeNotifier(
            context,
            Options.Create(new RealtimeOptions()),
            metrics,
            NullLogger<PgNotifyRealtimeNotifier>.Instance);

        var atTheCap = EventOfBodyBytes(PostgresPayloadLimitBytes);

        await Should.NotThrowAsync(() => notifier.NotifyAsync(UserId, atTheCap, ct));

        dropped.Count.ShouldBe(
            1,
            "the database would refuse this body, so the notifier must drop and count it, never send it");
    }

    [Fact]
    public async Task Notify_A_Body_One_Byte_Under_The_Cap_Is_Sent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var metrics = new RealtimeMetrics();
        using var dropped = new DropCounter(metrics);
        await using var context = Database.CreateAppDbContext();
        var notifier = new PgNotifyRealtimeNotifier(
            context,
            Options.Create(new RealtimeOptions()),
            metrics,
            NullLogger<PgNotifyRealtimeNotifier>.Instance);

        await Should.NotThrowAsync(
            () => notifier.NotifyAsync(UserId, EventOfBodyBytes(PostgresPayloadLimitBytes - 1), ct));

        dropped.Count.ShouldBe(0, "a body PostgreSQL accepts is written to the channel");
    }

    /// <summary>
    /// A <c>RenderProgress</c> whose serialized NOTIFY body is exactly <paramref name="bytes"/> UTF-8
    /// bytes: the stage is padded with the byte width PostgreSQL would measure.
    /// </summary>
    /// <param name="bytes">The exact body size to build.</param>
    private static RealtimeEvent EventOfBodyBytes(int bytes)
    {
        var probe = RealtimePayload.Build(
            UserId,
            new RealtimeEvent(
                RealtimeEventName.RenderProgress,
                new RealtimeIds(),
                RealtimeData.ForRenderProgress("x", 1)));
        var overhead = Encoding.UTF8.GetByteCount(probe) - 1;
        var @event = new RealtimeEvent(
            RealtimeEventName.RenderProgress,
            new RealtimeIds(),
            RealtimeData.ForRenderProgress(new string('x', bytes - overhead), 1));

        Encoding.UTF8.GetByteCount(RealtimePayload.Build(UserId, @event)).ShouldBe(bytes);

        return @event;
    }

    /// <summary>Counts the <c>realtime_notify_dropped_total</c> measurements of one meter instance.</summary>
    private sealed class DropCounter : IDisposable
    {
        private readonly MeterListener _listener;
        private int _count;

        public DropCounter(RealtimeMetrics metrics)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, owner) =>
                {
                    if (ReferenceEquals(instrument.Meter, metrics.Meter)
                        && string.Equals(
                            instrument.Name,
                            RealtimeMetrics.NotifyDroppedCounterName,
                            StringComparison.Ordinal))
                    {
                        owner.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref _count));
            _listener.Start();
        }

        public int Count => Volatile.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }
}
