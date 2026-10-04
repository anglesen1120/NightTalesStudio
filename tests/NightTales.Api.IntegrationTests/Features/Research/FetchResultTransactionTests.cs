using System.Diagnostics.Metrics;
using System.Net;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.Internal.Fetcher;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Application.Telemetry;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// PERF-1 and QA-9 of 02.5.2-T2: the result route writes the text object with no transaction open and no
/// <c>fetch_tasks</c> row lock held, answers 503 when that write outlives its own deadline (the task stays leased), and
/// emits its instruments only after the unit of work has committed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetchResultTransactionTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    private FetchTaskSeed NewSeed()
    {
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();

        return seed;
    }

    private ApiFactory NewFactory(IBlobStore blobs, TimeSpan? writeDeadline = null)
    {
        var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start)).WithFetcherInternalApi();
        factory.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton(blobs);
            if (writeDeadline is { } deadline)
            {
                services.RemoveAll<FetcherResultRunner>();
                services.AddSingleton(provider =>
                    new FetcherResultRunner(provider.GetRequiredService<IServiceScopeFactory>()) { TextWriteDeadline = deadline });
            }
        });

        return factory;
    }

    [Fact]
    public async Task Result_ObjectIsWrittenWithNoOpenTransaction_AndTheRowIsNotLocked()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "nolock.example");
        var inner = new InMemoryBlobStore();
        string? lockOutcome = null;
        long openTransactions = -1;
        var blobs = new ProbingBlobStore(inner)
        {
            BeforePut = _ =>
            {
                // A sweeper-style UPDATE of the very row: it must not wait on a lock held by the result command.
                try
                {
                    seed.Execute($"SET lock_timeout = '2s'; UPDATE fetch_tasks SET attempts = attempts WHERE id = '{task.TaskId}'");
                    lockOutcome = "not blocked";
                }
                catch (Exception exception)
                {
                    lockOutcome = exception.GetBaseException().Message;
                }

                openTransactions = seed.Count(
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state LIKE 'idle in transaction%'");

                return Task.CompletedTask;
            },
        };
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        blobs.Puts.ShouldBe(1);
        lockOutcome.ShouldBe("not blocked", "the PUT must not run under the fetch_tasks row lock");
        openTransactions.ShouldBe(0, "no session may sit idle in a transaction while the object is written");
        seed.TaskStatus(task.TaskId).ShouldBe("done");
        seed.SourceStatus(task.SourceId).ShouldBe("fetched");
    }

    [Fact]
    public async Task Result_ObjectWriteOutlivingItsDeadline_Returns503_AndTheTaskStaysLeased()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "slowstore.example");
        var blobs = new ProbingBlobStore(new InMemoryBlobStore())
        {
            BeforePut = token => Task.Delay(Timeout.Infinite, token),
        };
        using var factory = NewFactory(blobs, writeDeadline: TimeSpan.FromMilliseconds(300));
        using var client = factory.InternalClient();
        using var probe = new CounterProbe(factory.Services.GetRequiredService<NightTalesMetrics>().Meter);

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("service_unavailable");
        seed.TaskStatus(task.TaskId).ShouldBe("leased");
        seed.SourceStatus(task.SourceId).ShouldBe("pending");
        seed.Count($"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'").ShouldBe(0);
        probe.Names.ShouldBeEmpty("a result that was not accepted counts nothing");
    }

    [Fact]
    public async Task Result_LeaseLostDuringTheObjectWrite_Returns409_LeavesAnOrphan_AndCountsNothing()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "lostmid.example");
        var inner = new InMemoryBlobStore();
        var blobs = new ProbingBlobStore(inner)
        {
            BeforePut = _ =>
            {
                seed.Execute($"UPDATE fetch_tasks SET lease_until = now() - interval '1 minute' WHERE id = '{task.TaskId}'");

                return Task.CompletedTask;
            },
        };
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();
        using var probe = new CounterProbe(factory.Services.GetRequiredService<NightTalesMetrics>().Meter);

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("fetch_lease_lost");
        blobs.Puts.ShouldBe(1, "the object was written before the lock was taken: an orphan the 13.3 sweep removes");
        seed.SourceStatus(task.SourceId).ShouldBe("pending");
        seed.TaskStatus(task.TaskId).ShouldBe("leased");
        seed.Count($"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'").ShouldBe(0);
        probe.Names.ShouldBeEmpty();
    }

    [Fact]
    public async Task Result_CommitFailure_RollsBack_AndRecordsNoMetric()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "commitfail.example");
        seed.Execute(
            """
            CREATE OR REPLACE FUNCTION fail_done_commit_probe() RETURNS trigger AS $$
            BEGIN
                IF NEW.status = 'done' THEN
                    RAISE EXCEPTION 'forced commit failure';
                END IF;
                RETURN NULL;
            END $$ LANGUAGE plpgsql;
            CREATE CONSTRAINT TRIGGER fail_done_commit_probe AFTER UPDATE ON fetch_tasks
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_done_commit_probe();
            """);
        try
        {
            var blobs = new InMemoryBlobStore();
            using var factory = NewFactory(blobs);
            using var client = factory.InternalClient();
            using var probe = new CounterProbe(factory.Services.GetRequiredService<NightTalesMetrics>().Meter);

            using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());

            response.StatusCode.ShouldNotBe(HttpStatusCode.OK, "the deferred trigger refuses the commit");
            seed.TaskStatus(task.TaskId).ShouldBe("leased");
            seed.SourceStatus(task.SourceId).ShouldBe("pending");
            probe.Names.ShouldBeEmpty("no fetch_tasks_total and no fetch_duration_seconds for a transition that rolled back");
        }
        finally
        {
            seed.Execute("DROP TRIGGER IF EXISTS fail_done_commit_probe ON fetch_tasks; DROP FUNCTION IF EXISTS fail_done_commit_probe();");
        }
    }

    [Fact]
    public async Task Result_Committed_CountsOnce_WithALeaseToResultDuration_AndADuplicateWritesAndCountsNothing()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "counted.example");
        seed.Execute($"UPDATE fetch_tasks SET leased_at = now() - interval '20 seconds' WHERE id = '{task.TaskId}'");
        var blobs = new ProbingBlobStore(new InMemoryBlobStore());
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();
        using var probe = new CounterProbe(factory.Services.GetRequiredService<NightTalesMetrics>().Meter);

        using var first = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());
        using var second = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        blobs.Puts.ShouldBe(1, "a duplicate is refused by the lease peek before any object is written");
        probe.Count(NightTalesMetrics.FetchTasksTotalName).ShouldBe(1);
        var seconds = probe.Values(NightTalesMetrics.FetchDurationSecondsName).ShouldHaveSingleItem();
        seconds.ShouldBeInRange(19d, 120d);
    }

    [Fact]
    public async Task Result_TamperedHash_Returns400_WritesNoObject()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "tamper.example");
        var blobs = new ProbingBlobStore(new InMemoryBlobStore());
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();
        var body = FetchTaskSeed.DoneBody();
        body["textSha256"] = FetchTaskSeed.Sha256("another text");

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        blobs.Puts.ShouldBe(0);
        seed.TaskStatus(task.TaskId).ShouldBe("leased");
    }

    private sealed class CounterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Name, double Value)> _measures = [];

        public CounterProbe(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)
                    && (instrument.Name == NightTalesMetrics.FetchTasksTotalName
                        || instrument.Name == NightTalesMetrics.FetchDurationSecondsName))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Add(instrument.Name, value));
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Add(instrument.Name, value));
            _listener.Start();
        }

        public IReadOnlyList<string> Names
        {
            get
            {
                lock (_measures)
                {
                    return [.. _measures.Select(measure => measure.Name)];
                }
            }
        }

        public int Count(string name) => Names.Count(candidate => candidate == name);

        public IReadOnlyList<double> Values(string name)
        {
            lock (_measures)
            {
                return [.. _measures.Where(measure => measure.Name == name).Select(measure => measure.Value)];
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Add(string name, double value)
        {
            lock (_measures)
            {
                _measures.Add((name, value));
            }
        }
    }

    /// <summary>Delegates to an in-memory store and runs a hook just before every research-object <c>PutAsync</c>.</summary>
    private sealed class ProbingBlobStore(InMemoryBlobStore inner) : IBlobStore
    {
        private int _puts;

        public Func<CancellationToken, Task>? BeforePut { get; init; }

        public int Puts => Volatile.Read(ref _puts);

        public async Task<ErrorOr<BlobInfo>> PutAsync(
            string bucket,
            string key,
            Stream content,
            string contentType,
            CancellationToken cancellationToken)
        {
            if (key.StartsWith("research/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _puts);
                if (BeforePut is { } hook)
                {
                    await hook(cancellationToken).ConfigureAwait(false);
                }
            }

            return await inner.PutAsync(bucket, key, content, contentType, cancellationToken).ConfigureAwait(false);
        }

        public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(bucket, key, cancellationToken);

        public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, long fromByte, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(bucket, key, fromByte, cancellationToken);

        public Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(bucket, key, cancellationToken);

        public Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken) =>
            inner.ExistsAsync(bucket, key, cancellationToken);

        public IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken) =>
            inner.ListKeysAsync(bucket, prefix, cancellationToken);

        public ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName) =>
            inner.CreateReadUrl(bucket, key, ttl, downloadFileName);
    }
}
