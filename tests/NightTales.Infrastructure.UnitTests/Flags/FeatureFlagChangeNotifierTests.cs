using System.Text.Json;
using NightTales.Application.Abstractions.Flags;
using NightTales.Domain.Platform.Events;
using NightTales.Infrastructure.Persistence.Flags;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.TestKit.Realtime;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Flags;

/// <summary>
/// The writer's eviction notifier (09 README BD13, 09.9.2-T2 §Eviction): one
/// <c>SELECT pg_notify('feature_flags_changed', &lt;key&gt;)</c> <em>and</em> one
/// <c>feature_flags_changed.v1</c> outbox row, both on the caller's context so both join the writer's
/// transaction.
/// </summary>
/// <remarks>
/// The context is a real <c>AppDbContext</c> over the real provider model with the two provider calls a
/// database would serve intercepted (09 README D13), so the SQL contract is asserted without a server and
/// the queued row is read off the change tracker — the row itself is committed by the command's own
/// <c>SaveChanges</c>.
/// </remarks>
public sealed class FeatureFlagChangeNotifierTests
{
    private const string Key = "youtube_publish";

    [Fact]
    public async Task Notify_issues_one_pg_notify_with_the_key_as_its_parameter()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RecordingDbConnectionFactory();
        await using var context = factory.CreateContext();
        var notifier = Build(context);

        await notifier.NotifyChangedAsync(Key, enabled: true, ct);

        factory.Commands.Count.ShouldBe(1, "one change is one round trip; the outbox row rides the commit");

        var command = factory.SingleCommand;
        command.CommandText.ShouldMatch(@"^SELECT pg_notify\('feature_flags_changed', @\w+\)$");
        command.CommandText.ShouldNotContain("{0}", Case.Sensitive, "the placeholder must be a real parameter");
        command.SingleParameter.ShouldBe(Key);
    }

    /// <summary>
    /// The second eviction path is not dead: the call queues exactly one platform outbox row whose payload
    /// the Worker's handler can deserialize back into the event.
    /// </summary>
    [Fact]
    public async Task Notify_queues_one_platform_outbox_row_the_handler_can_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RecordingDbConnectionFactory();
        await using var context = factory.CreateContext();
        var clock = new FakeClock();
        var notifier = Build(context, clock);

        await notifier.NotifyChangedAsync(Key, enabled: true, ct);

        var row = context.ChangeTracker.Entries<OutboxMessage>().ShouldHaveSingleItem().Entity;

        row.Type.ShouldBe("feature_flags_changed.v1");
        row.UserId.ShouldBeNull("it is a [PlatformEvent], so the dispatcher pins no tenant");
        row.OccurredAt.ShouldBe(clock.UtcNow);
        row.NextAttemptAt.ShouldBe(clock.UtcNow, "the first delivery attempt is the first poll after the commit");
        row.Attempts.ShouldBe(0);

        var delivered = JsonSerializer.Deserialize<FeatureFlagsChanged>(row.Payload, OutboxJson.Options);
        delivered.ShouldNotBeNull();
        delivered!.Key.ShouldBe(Key);
        delivered.Enabled.ShouldBeTrue();
        delivered.OccurredAt.ShouldBe(clock.UtcNow);
    }

    /// <summary>
    /// The channel is one constant: the notifier's literal and the invalidator's <c>LISTEN</c> cannot drift
    /// apart without failing here.
    /// </summary>
    [Fact]
    public void The_notify_sql_names_the_one_changed_channel()
    {
        PgFeatureFlagChangeNotifier.NotifySql.ShouldContain(FeatureFlagsCache.ChangedChannel);
        PgFeatureFlagChangeNotifier.NotifySql.ShouldStartWith("SELECT pg_notify(");
    }

    private static PgFeatureFlagChangeNotifier Build(
        NightTales.Infrastructure.Persistence.AppDbContext context,
        FakeClock? clock = null) =>
        new(context, new OutboxEventRegistry(), clock ?? new FakeClock());
}
