using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit.Realtime;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Realtime;

/// <summary>
/// The notify contract of 09 README BD11 and [RULED — README §B10 WT54]: the body of a real-time event is
/// the frozen JSON <c>{ userId, event, ids, data }</c> — ids only, <c>data</c> only for
/// <see cref="RealtimeEventName.RenderProgress"/> and holding nothing but <c>stage</c> + <c>percent</c> —
/// and the notifier writes it as exactly one <c>SELECT pg_notify('realtime', …)</c> with no transaction
/// of its own.
/// </summary>
/// <remarks>
/// The recording connection of <see cref="RecordingDbConnectionFactory"/> replaces the server, not the
/// provider path, so what is asserted here is the statement PostgreSQL would receive.
/// </remarks>
public sealed class PgNotifyPayloadTests
{
    private static readonly Guid UserId = Guid.Parse("018f2c58-0f0c-7a5b-8f7a-1c2d3e4f5a60");
    private static readonly Guid SeriesId = Guid.Parse("018f2c58-1111-7a5b-8f7a-1c2d3e4f5a61");
    private static readonly Guid EpisodeId = Guid.Parse("018f2c58-2222-7a5b-8f7a-1c2d3e4f5a62");
    private static readonly Guid RenderJobId = Guid.Parse("018f2c58-3333-7a5b-8f7a-1c2d3e4f5a63");

    [Fact]
    public void Payload_Contains_UserId_Event_Ids_And_Ruled_Data_Only()
    {
        var episodeUpdated = RealtimePayload.Build(
            UserId,
            new RealtimeEvent(
                RealtimeEventName.EpisodeUpdated,
                new RealtimeIds(SeriesId: SeriesId, EpisodeId: EpisodeId)));

        using (var document = JsonDocument.Parse(episodeUpdated))
        {
            var root = document.RootElement;

            Members(root).ShouldBe("userId,event,ids,data");
            root.GetProperty("userId").GetGuid().ShouldBe(UserId);
            root.GetProperty("event").GetString().ShouldBe("episode_updated");
            Members(root.GetProperty("ids")).ShouldBe("seriesId,episodeId");
            root.GetProperty("ids").GetProperty("seriesId").GetGuid().ShouldBe(SeriesId);
            root.GetProperty("ids").GetProperty("episodeId").GetGuid().ShouldBe(EpisodeId);

            // Every event but RenderProgress carries data: null, so a client can never read ruled data
            // off an event the ruling does not allow it on.
            root.GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        // The same rule holds when a producer passes data by mistake: the ruling wins over the caller.
        var mistake = RealtimePayload.Build(
            UserId,
            new RealtimeEvent(
                RealtimeEventName.CreditBalanceChanged,
                new RealtimeIds(),
                RealtimeData.ForRenderProgress("voice", 50)));

        using (var document = JsonDocument.Parse(mistake))
        {
            document.RootElement.GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Null);
            Members(document.RootElement.GetProperty("ids")).ShouldBeEmpty();
        }

        var progress = RealtimePayload.Build(
            UserId,
            new RealtimeEvent(
                RealtimeEventName.RenderProgress,
                new RealtimeIds(RenderJobId: RenderJobId),
                RealtimeData.ForRenderProgress("voice", 42)));

        using (var document = JsonDocument.Parse(progress))
        {
            var root = document.RootElement;

            root.GetProperty("event").GetString().ShouldBe("render_progress");
            Members(root.GetProperty("ids")).ShouldBe("renderJobId");

            var data = root.GetProperty("data");
            Members(data).ShouldBe("stage,percent");
            data.GetProperty("stage").GetString().ShouldBe("voice");
            data.GetProperty("percent").GetInt16().ShouldBe((short)42);
        }

        // The two ends of the ruled range are carried verbatim; outside it the producer is refused.
        RealtimeData.ForRenderProgress("queued", 0).Percent.ShouldBe((short)0);
        RealtimeData.ForRenderProgress("checking", 100).Percent.ShouldBe((short)100);
        Should.Throw<ArgumentOutOfRangeException>(() => RealtimeData.ForRenderProgress("voice", 101));
        Should.Throw<ArgumentOutOfRangeException>(() => RealtimeData.ForRenderProgress("voice", -1));
        Should.Throw<ArgumentException>(() => RealtimeData.ForRenderProgress("  ", 10));
    }

    [Fact]
    public async Task Payload_Under_Limit_Is_Sent()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RecordingDbConnectionFactory();
        using var context = factory.CreateContext();
        using var metrics = new RealtimeMetrics();
        using var recorded = new MetricRecorder(metrics);
        var notifier = CreateNotifier(context, metrics);

        var @event = new RealtimeEvent(
            RealtimeEventName.EpisodeUpdated,
            new RealtimeIds(SeriesId: SeriesId, EpisodeId: EpisodeId));

        await notifier.NotifyAsync(UserId, @event, ct);

        factory.Commands.Count.ShouldBe(1, "one event is one round trip (BD11)");

        var command = factory.SingleCommand;
        command.CommandText.ShouldMatch(@"^SELECT pg_notify\('realtime', @\w+\)$");
        command.CommandText.ShouldNotContain("{0}", Case.Sensitive, "the placeholder must be a real parameter");
        command.ParameterValues.Count.ShouldBe(1);
        command.SingleParameter.ShouldBe(RealtimePayload.Build(UserId, @event));
        RealtimePayload.FitsLimit(command.SingleParameter).ShouldBeTrue();

        recorded.Measurements.ShouldContain((RealtimeMetrics.NotifyCounterName, "episode_updated"));
        recorded.Measurements.ShouldNotContain(
            (RealtimeMetrics.NotifyDroppedCounterName, "episode_updated"),
            "an ids-only body is far below the cap");
    }

    [Fact]
    public async Task Payload_Over_8000_Bytes_Is_Dropped_And_Counted()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RecordingDbConnectionFactory();
        using var context = factory.CreateContext();
        using var metrics = new RealtimeMetrics();
        using var recorded = new MetricRecorder(metrics);
        var notifier = CreateNotifier(context, metrics);

        // An ids-only body cannot reach the cap in practice, so the drop is proven with a synthetic
        // RenderProgress body that does.
        var oversized = new RealtimeEvent(
            RealtimeEventName.RenderProgress,
            new RealtimeIds(RenderJobId: RenderJobId),
            RealtimeData.ForRenderProgress(new string('x', RealtimePayload.MaxBytes), 10));

        RealtimePayload.FitsLimit(RealtimePayload.Build(UserId, oversized)).ShouldBeFalse(
            "the synthetic body must exceed the cap for this test to mean anything");

        await notifier.NotifyAsync(UserId, oversized, ct);

        factory.Commands.ShouldBeEmpty("an over-sized body is dropped, never truncated (BD11/BD16)");
        recorded.Measurements.ShouldContain((RealtimeMetrics.NotifyDroppedCounterName, "render_progress"));
        recorded.Measurements.ShouldNotContain(
            (RealtimeMetrics.NotifyCounterName, "render_progress"),
            "nothing was written for the dropped event");
    }

    [Fact]
    public async Task Notify_Uses_No_Transaction_Of_Its_Own()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RecordingDbConnectionFactory();
        using var context = factory.CreateContext();
        using var metrics = new RealtimeMetrics();
        var notifier = CreateNotifier(context, metrics);

        await notifier.NotifyAsync(
            UserId,
            new RealtimeEvent(RealtimeEventName.PublicationUpdated, new RealtimeIds()),
            ct);

        // The one statement is a SELECT on the caller's context: no BEGIN/COMMIT, no INSERT/UPDATE, no
        // SaveChanges — so inside a command it is the caller's transaction that decides delivery (D5).
        var command = factory.SingleCommand;
        command.CommandText.ShouldStartWith("SELECT");
        command.CommandText.ShouldNotContain("BEGIN", Case.Sensitive);
        command.CommandText.ShouldNotContain("COMMIT", Case.Sensitive);
        command.CommandText.ShouldNotContain(";", Case.Sensitive, "one statement per notification");

        context.Database.CurrentTransaction.ShouldBeNull();
        context.ChangeTracker.HasChanges().ShouldBeFalse("the notifier writes no row");
    }

    [Fact]
    public void Producer_BuildsTheEvent_WithApplicationTypesOnly()
    {
        // 02/06/09.6.3 build the event and call the notifier from Application code, so the whole producer
        // contract — the event, its ids, its data and the port — must live in and be built from the
        // Application assembly (09.8.1 plan §5). The assembly itself always references NightTales.Domain
        // (NightTales.Architecture.Tests allows exactly that), so what is asserted here is the contract's
        // public surface: a Domain, Infrastructure or Api type in a member signature would be a leak the
        // architecture allow-list cannot see.
        Type[] contract =
        [
            typeof(RealtimeEvent),
            typeof(RealtimeIds),
            typeof(RealtimeData),
            typeof(RealtimeEventName),
            typeof(RealtimeEventNames),
            typeof(RealtimePayload),
            typeof(IRealtimeNotifier),
        ];

        contract.ShouldAllBe(type => type.Assembly.GetName().Name == "NightTales.Application");

        contract
            .SelectMany(SignatureTypes)
            .SelectMany(Flatten)
            .Select(type => (Type: type, Assembly: type.Assembly.GetName().Name))
            .Where(member => member.Assembly?.StartsWith("NightTales", StringComparison.Ordinal) == true
                && !string.Equals(member.Assembly, "NightTales.Application", StringComparison.Ordinal))
            .Select(member => $"{member.Type.FullName} ({member.Assembly})")
            .Distinct(StringComparer.Ordinal)
            .ShouldBeEmpty("the producer contract must be built from Application and BCL types only");
    }

    [Fact]
    public void Payload_Cap_Is_Exclusive_Because_Postgres_Refuses_A_Payload_As_Long_As_Its_Limit()
    {
        // PostgreSQL 17 accepts a payload shorter than 8000 bytes and refuses one of exactly 8000
        // (`22023 payload string too long`). RealtimePayload.MaxBytes / RealtimeOptions.MaxPayloadBytes
        // default to that same number, so the last body a notifier may send is 7999 bytes long.
        const int PostgresPayloadLimitBytes = 8_000;

        var underTheLimit = BodyOfExactBytes(PostgresPayloadLimitBytes - 1);
        var atTheLimit = BodyOfExactBytes(PostgresPayloadLimitBytes);

        Encoding.UTF8.GetByteCount(underTheLimit).ShouldBe(PostgresPayloadLimitBytes - 1);
        Encoding.UTF8.GetByteCount(atTheLimit).ShouldBe(PostgresPayloadLimitBytes);

        RealtimePayload.FitsLimit(underTheLimit).ShouldBeTrue();
        RealtimePayload.FitsLimit(atTheLimit).ShouldBeFalse(
            "PostgreSQL refuses a payload of exactly 8000 bytes, so the notifier must drop it (BD11/BD16)");
    }

    /// <summary>Every type the public members of <paramref name="type"/> are declared with.</summary>
    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(Declared))
        {
            yield return property.PropertyType;
        }

        foreach (var field in type.GetFields(Declared))
        {
            yield return field.FieldType;
        }

        foreach (var method in type.GetMethods(Declared).Where(method => !method.IsSpecialName))
        {
            yield return method.ReturnType;

            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var parameter in type.GetConstructors().SelectMany(constructor => constructor.GetParameters()))
        {
            yield return parameter.ParameterType;
        }
    }

    /// <summary>
    /// <paramref name="type"/> plus the element and generic-argument types it is built from, so
    /// <c>Guid?</c>, <c>RealtimeData?</c> and an array or generic of a Domain type all resolve.
    /// </summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in Flatten(element))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }

    private static PgNotifyRealtimeNotifier CreateNotifier(AppDbContext context, RealtimeMetrics metrics) =>
        new(context, Options.Create(new RealtimeOptions()), metrics, NullLogger<PgNotifyRealtimeNotifier>.Instance);

    private static string Members(JsonElement element) =>
        string.Join(",", element.EnumerateObject().Select(property => property.Name));

    /// <summary>The NOTIFY body of a <c>RenderProgress</c> sized exactly <paramref name="bytes"/> bytes.</summary>
    private static string BodyOfExactBytes(int bytes)
    {
        var probe = RealtimePayload.Build(
            UserId,
            new RealtimeEvent(
                RealtimeEventName.RenderProgress,
                new RealtimeIds(),
                RealtimeData.ForRenderProgress("x", 1)));
        var overhead = Encoding.UTF8.GetByteCount(probe) - 1;

        return RealtimePayload.Build(
            UserId,
            new RealtimeEvent(
                RealtimeEventName.RenderProgress,
                new RealtimeIds(),
                RealtimeData.ForRenderProgress(new string('x', bytes - overhead), 1)));
    }

    /// <summary>Collects the counters of one meter instance, like 09.9.3-T1's exporter would.</summary>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly List<(string Instrument, string Tag)> _measurements = [];
        private readonly MeterListener _listener;

        public MetricRecorder(RealtimeMetrics metrics)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, owner) =>
                {
                    if (ReferenceEquals(instrument.Meter, metrics.Meter))
                    {
                        owner.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, RealtimeMetrics.EventTagName, StringComparison.Ordinal))
                    {
                        _measurements.Add((instrument.Name, tag.Value as string ?? string.Empty));
                    }
                }
            });

            _listener.Start();
        }

        public IReadOnlyList<(string Instrument, string Tag)> Measurements => _measurements;

        public void Dispose() => _listener.Dispose();
    }
}
