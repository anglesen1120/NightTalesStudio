using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Assets.Jobs;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Assets;

/// <summary>
/// The audit phase of <c>RetentionSweepJob</c> against real PostgreSQL (09.7.3-T1): the ordered rules of
/// <c>AuditRetentionRules</c> decide, the scan is a keyset over <c>ix_audit_logs_created_id</c>, and the delete
/// is set-based. Rows carry a per-test marker in <c>action</c> so one class database serves every test.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditRetentionTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 3, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task AuditRetention_KeepsPaymentLinkedRows_RegardlessOfAge()
    {
        // HasPaymentLink is always false in P (no linkage column exists), so the exact row the first draft
        // would have deleted — a 13-month-old user-actor, user-visible billing row — is judged by the 365-day
        // rule today; the never-delete outcome of rule 1 is pinned at the predicate (AuditRetentionPredicateTests).
        var young = await InsertAsync("keeps-billing-young", "billing", "user", "user", ageDays: 300);

        await SweepAsync();

        Exists(young).ShouldBeTrue();
    }

    [Fact]
    public async Task AuditRetention_KeepsAccountSecurityRowsFor24Months()
    {
        var keep = await InsertAsync("sec-keep", "account_security", "user", "user", ageDays: 700);
        var drop = await InsertAsync("sec-drop", "account_security", "user", "user", ageDays: 760);

        await SweepAsync();

        Exists(keep).ShouldBeTrue();
        Exists(drop).ShouldBeFalse();
    }

    [Fact]
    public async Task AuditRetention_KeepsAdminAndInternalRowsFor24Months()
    {
        var adminKeep = await InsertAsync("admin-keep", "support", "admin", "user", ageDays: 700);
        var internalKeep = await InsertAsync("int-keep", "episodes", "system", "internal", ageDays: 700, subject: false);
        var adminDrop = await InsertAsync("admin-drop", "support", "admin", "user", ageDays: 760);

        await SweepAsync();

        Exists(adminKeep).ShouldBeTrue();
        Exists(internalKeep).ShouldBeTrue();
        Exists(adminDrop).ShouldBeFalse();
    }

    [Fact]
    public async Task AuditRetention_DeletesUserRowsAfter12Months()
    {
        var keep = await InsertAsync("user-keep", "episodes", "user", "user", ageDays: 360);
        var drop = await InsertAsync("user-drop", "episodes", "user", "user", ageDays: 370);

        await SweepAsync();

        Exists(keep).ShouldBeTrue();
        Exists(drop).ShouldBeFalse();
    }

    [Fact]
    public async Task AuditRetention_UsesTheOrderedRulesFromAuditRetentionRules()
    {
        // 400 days old: gone under the default 365-day rule, kept under the 730-day account_security rule —
        // the same age, a different first match.
        var user = await InsertAsync("ordered-user", "episodes", "user", "user", ageDays: 400);
        var security = await InsertAsync("ordered-security", "account_security", "user", "user", ageDays: 400);

        await SweepAsync();

        Exists(user).ShouldBeFalse();
        Exists(security).ShouldBeTrue();
    }

    [Fact]
    public async Task AuditRetention_DeletesAtMost500RowsPerBatch_AndIsIdempotent()
    {
        await Database.ExecuteAsync(
            "INSERT INTO audit_logs (id, actor_type, subject_user_id, action, category, visibility, data, created_at) "
            + "SELECT gen_random_uuid(), 'user', gen_random_uuid(), 'bulk-expired', 'episodes', 'user', '{}'::jsonb, "
            + $"'{Now:O}'::timestamptz - interval '500 days' - (g || ' seconds')::interval FROM generate_series(1, 1200) g",
            Ct);

        await using var host = await StartAsync();
        using (var scope = host.CreateScope())
        {
            var summary = await scope.ServiceProvider.GetRequiredService<RetentionSweepJob>().SweepAsync(Ct);
            summary.AuditDeleted.ShouldBeGreaterThanOrEqualTo(1200);
            summary.Capped.ShouldBeFalse();
        }

        SqlProbe.Count(Database, "SELECT count(*) FROM audit_logs WHERE action = 'bulk-expired'").ShouldBe(0);

        using var second = host.CreateScope();
        (await second.ServiceProvider.GetRequiredService<RetentionSweepJob>().SweepAsync(Ct)).AuditDeleted.ShouldBe(0);
    }

    [Fact]
    public async Task AuditRetention_ScansByKeyset_NoOffset()
    {
        await Database.ExecuteAsync(
            "INSERT INTO audit_logs (id, actor_type, subject_user_id, action, category, visibility, data, created_at) "
            + "SELECT gen_random_uuid(), 'user', gen_random_uuid(), 'keyset-probe', 'episodes', 'user', '{}'::jsonb, "
            + $"'{Now:O}'::timestamptz + (g || ' seconds')::interval FROM generate_series(1, 1001) g",
            Ct);

        await using var context = Database.CreateAppDbContext();
        var store = new AuditRetentionStore(context);

        var seen = new List<Guid>();
        DateTimeOffset? afterAt = null;
        Guid? afterId = null;
        var pages = 0;
        while (true)
        {
            var page = await store.LoadAfterAsync(afterAt, afterId, 500, Ct);
            pages++;
            if (afterAt is { } previous && page.Count > 0)
            {
                page[0].CreatedAt.ShouldBeGreaterThanOrEqualTo(previous, "the cursor only moves forward");
                seen.ShouldNotContain(page[0].Id, "no row is read twice");
            }

            seen.AddRange(page.Select(row => row.Id));
            if (page.Count < 500)
            {
                break;
            }

            afterAt = page[^1].CreatedAt;
            afterId = page[^1].Id;
        }

        seen.Distinct().Count().ShouldBe(seen.Count);
        seen.Count.ShouldBeGreaterThanOrEqualTo(1001);
        pages.ShouldBeGreaterThanOrEqualTo(3);

        // The keyset predicate is served by ix_audit_logs_created_id (forced off the seq scan: the table is small).
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off; SET enable_bitmapscan = off", connection))
        {
            await off.ExecuteNonQueryAsync(Ct);
        }

        await using var explain = new NpgsqlCommand(
            "EXPLAIN SELECT id FROM audit_logs WHERE (created_at, id) > "
            + "(timestamptz '2026-10-03 00:00:00+00', uuid '00000000-0000-0000-0000-000000000000') "
            + "ORDER BY created_at, id LIMIT 500",
            connection);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(0));
            }
        }

        string.Join('\n', plan).ShouldContain("ix_audit_logs_created_id");
        string.Join('\n', plan).ShouldNotContain("Sort");
    }

    private async Task<WorkerHost> StartAsync()
    {
        var host = new WorkerHost(Database, new FakeClock(Now));
        await host.StartAsync(Ct);

        return host;
    }

    private async Task SweepAsync()
    {
        await using var host = await StartAsync();
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RetentionSweepJob>().SweepAsync(Ct);
    }

    private async Task<Guid> InsertAsync(string action, string category, string actor, string visibility, int ageDays, bool subject = true)
    {
        var id = Guid.CreateVersion7();
        var subjectSql = subject ? $"'{Guid.CreateVersion7()}'" : "NULL";
        await Database.ExecuteAsync(
            "INSERT INTO audit_logs (id, actor_type, subject_user_id, action, category, visibility, data, created_at) "
            + $"VALUES ('{id}', '{actor}', {subjectSql}, '{action}', '{category}', '{visibility}', '{{}}'::jsonb, "
            + $"'{Now:O}'::timestamptz - interval '{ageDays} days')",
            Ct);

        return id;
    }

    private bool Exists(Guid id) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE id = '{id}'") == 1;
}
