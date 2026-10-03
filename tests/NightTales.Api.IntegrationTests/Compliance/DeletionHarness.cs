using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Application.Features.Compliance.Deletion.Jobs;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Compliance;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>One captured log line: level, rendered message and the logger category.</summary>
internal sealed record CapturedLog(LogLevel Level, string Category, string Message);

/// <summary>Collects every log line of the host (the job logs ids, scopes and counts only).</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _lines = new();

    public IReadOnlyCollection<CapturedLog> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Sink(string category, ConcurrentQueue<CapturedLog> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(new CapturedLog(logLevel, category, formatter(state, exception)));
    }
}

/// <summary>
/// The Worker graph (09 README D13) with a <see cref="FakeClock"/>, an <see cref="InMemoryBlobStore"/>, and a scripted
/// handler for the <c>channel</c> scope (whose real owner, 06.4.3-T1, has not landed), so the deletion engine runs as in
/// production. The <c>episode</c> handler is the real one.
/// </summary>
internal sealed class DeletionHarness : IAsyncDisposable
{
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private MeterListener? _listener;

    private DeletionHarness(WorkerHost host, FakeClock clock, InMemoryBlobStore blobs, DeletionHandlerProbe probe, CapturingLoggerProvider logs)
    {
        Host = host;
        Clock = clock;
        Blobs = blobs;
        Probe = probe;
        Logs = logs;
    }

    internal WorkerHost Host { get; }

    internal FakeClock Clock { get; }

    internal InMemoryBlobStore Blobs { get; }

    internal DeletionHandlerProbe Probe { get; }

    internal CapturingLoggerProvider Logs { get; }

    internal static async Task<DeletionHarness> StartAsync(
        TestDatabase database,
        bool channelHandler = true,
        Action<WorkerHost>? configure = null,
        CancellationToken ct = default)
    {
        // The class database is shared by its tests: every test starts from an empty deletion ledger.
        await database.ExecuteAsync("DELETE FROM data_deletion_requests; DELETE FROM audit_logs WHERE entity_type = 'deletion_request'", ct);

        var clock = new FakeClock();
        var blobs = new InMemoryBlobStore();
        var probe = new DeletionHandlerProbe();
        var logs = new CapturingLoggerProvider();

        var host = new WorkerHost(database, clock);
        host.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(blobs);
            services.AddSingleton(probe);
            services.AddLogging(builder => builder.AddProvider(logs));

            if (channelHandler)
            {
                services.AddScoped<IDeletionTargetOwnership>(_ => new FakeDeletionTargetOwnership(DeletionScope.Channel));
                services.AddScoped<IDeletionScopeHandler>(provider => new FakeDeletionScopeHandler(
                    DeletionScope.Channel,
                    provider.GetRequiredService<DeletionHandlerProbe>(),
                    provider.GetRequiredService<IAppDbContext>(),
                    provider.GetRequiredService<ICurrentUser>()));
            }
        });

        if (channelHandler)
        {
            host.WithSetting("DataDeletion:DeferredScopes:0", "account");
        }

        configure?.Invoke(host);
        await host.StartAsync(ct);

        var harness = new DeletionHarness(host, clock, blobs, probe, logs);
        harness.Listen();

        return harness;
    }

    internal async Task RunJobAsync(CancellationToken ct)
    {
        using var scope = Host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DataDeletionJob>().RunAsync(ct);
    }

    /// <summary>Runs <c>CreateAsync</c> the way a command does: pinned to the user, inside the unit of work.</summary>
    internal async Task<ErrorOr<DeletionRequest>> CreateAsync(
        Guid userId,
        DeletionScope scope,
        Guid? targetId,
        DeletionReason reason,
        CancellationToken ct)
    {
        using var serviceScope = Host.CreateScope();
        using var pinned = serviceScope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
        var unit = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unit.BeginAsync(ct);
        var created = await serviceScope.ServiceProvider.GetRequiredService<IDeletionRequests>()
            .CreateAsync(userId, scope, targetId, reason, ct);

        if (created.IsError)
        {
            await unit.RollbackAsync(ct);

            return created;
        }

        (await unit.CommitAsync(ct)).IsError.ShouldBeFalse();

        return created;
    }

    /// <summary>Counters accumulated since the host started plus the gauges' current values, keyed <c>name</c> or <c>name{tag=value,…}</c>.</summary>
    internal Dictionary<string, long> Metrics()
    {
        _listener!.RecordObservableInstruments();

        lock (_counters)
        {
            return new Dictionary<string, long>(_counters, StringComparer.Ordinal);
        }
    }

    private void Listen()
    {
        var metrics = Host.Services.GetRequiredService<DataDeletionMetrics>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var key = tags.Length == 0
                ? instrument.Name
                : $"{instrument.Name}{{{string.Join(',', tags.ToArray().OrderBy(tag => tag.Key, StringComparer.Ordinal).Select(tag => $"{tag.Key}={tag.Value}"))}}}";

            lock (_counters)
            {
                // Gauges report their current value on each observation; counters add up.
                _counters[key] = instrument is ObservableGauge<long> ? measurement : _counters.GetValueOrDefault(key) + measurement;
            }
        });
        listener.Start();
        _listener = listener;
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Dispose();
        await Host.DisposeAsync();
    }
}
