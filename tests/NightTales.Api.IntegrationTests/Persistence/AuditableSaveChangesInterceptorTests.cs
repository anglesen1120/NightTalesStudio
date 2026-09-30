using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Platform;
using NightTales.Domain.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Persistence;

/// <summary>
/// <c>AuditableSaveChangesInterceptor</c> (09 README D8, 09 W3 CR-38): one clock stamps both audit
/// columns, a factory that already received <c>now</c> is never overwritten, and an update moves only
/// <c>updated_at</c>. The values are read back from PostgreSQL, so the mapping is proven too.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditableSaveChangesInterceptorTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task AuditableSaveChanges_StampsCreatedAtAndUpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        // 1. An insert with default timestamps gets both from the clock (a core aggregate, through the
        //    same tracked path a command handler uses).
        var series = await new SeriesBuilder(Database, new TestCurrentUser(), clock).CreateAsync(user.Id, ct: ct);

        (string CreatedAt, string UpdatedAt) row = ReadAudit(Database, "series", series.Id.ToString());

        row.CreatedAt.ShouldBe(Stamp(clock.UtcNow));
        row.UpdatedAt.ShouldBe(Stamp(clock.UtcNow));

        // 2. A factory that already received `now` keeps its value (ICreatedAt only: no updated_at).
        var supplied = clock.UtcNow.AddDays(-30);
        var record = UsageRecord.Record(user.Id, UsageOperation.Story, "openai", 0.25m, supplied);

        await using (var insert = Database.CreateAppDbContext(clock: clock))
        {
            insert.UsageRecords.Add(record);
            await insert.SaveChangesAsync(ct);
        }

        ReadCreatedAt(Database, "usage_records", record.Id.ToString()).ShouldBe(Stamp(supplied));

        // 3. An update bumps only updated_at, on an IAuditable row.
        var flag = FeatureFlag.Create($"flag_{Guid.CreateVersion7():N}");

        await using (var insert = Database.CreateAppDbContext(clock: clock))
        {
            insert.FeatureFlags.Add(flag);
            await insert.SaveChangesAsync(ct);
        }

        clock.Advance(TimeSpan.FromHours(2));

        await using (var update = Database.CreateAppDbContext(clock: clock))
        {
            update.FeatureFlags.Attach(flag);
            update.Entry(flag).Property(nameof(FeatureFlag.Enabled)).CurrentValue = true;
            await update.SaveChangesAsync(ct);
        }

        await using (var verify = Database.CreateAppDbContext(clock: clock))
        {
            var updated = await verify.FeatureFlags.SingleAsync(candidate => candidate.Key == flag.Key, ct);

            updated.CreatedAt.ShouldBe(clock.UtcNow.AddHours(-2), "an update never moves created_at");
            updated.UpdatedAt.ShouldBe(clock.UtcNow, "an update stamps updated_at from the clock");
        }
    }

    private static string Stamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static (string CreatedAt, string UpdatedAt) ReadAudit(TestDatabase database, string table, string id)
        => (
            ReadCreatedAt(database, table, id),
            SqlProbe.ScalarText(
                database,
                $"SELECT to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM {table} WHERE id = '{id}'"));

    /// <summary>The <c>created_at</c> of one row — the only timestamp an append-only table carries.</summary>
    private static string ReadCreatedAt(TestDatabase database, string table, string id)
        => SqlProbe.ScalarText(
            database,
            $"SELECT to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM {table} WHERE id = '{id}'");
}
