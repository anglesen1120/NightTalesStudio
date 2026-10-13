using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// The optimistic-concurrency behaviour of the fact-sheet routes against the real host and a real PostgreSQL 17
/// (02.5.4-T1, D9/F12): the episode row lock (<c>FOR NO KEY UPDATE</c>) plus the <c>fs-</c> ETag make exactly
/// one of N parallel saves win, exactly one of N parallel approvals land, and a unique violation the database
/// raises under that race (the <c>ux_fact_sheets_episode_approved</c> / <c>ux_fact_sheets_episode_version</c>
/// index) an answered 412 <c>fact_sheet_conflict</c> — never a 500.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactSheetConcurrencyTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task PutFactSheet_FourParallelSavesWithTheSameEtag_OneWins_AndTheRestGet412()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);
        var etag = FactSheetHarness.Etag(sheet.Id);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        var responses = await Task.WhenAll(Enumerable.Range(1, 4).Select(number => FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            etag,
            FactSheetHarness.Body(
                FactSheetHarness.Fact("f1", "One fact.", [source]),
                FactSheetHarness.Fact(null, $"A fact added by tab {number}.", [source])))));

        var statuses = responses.Select(response => response.StatusCode).ToArray();
        foreach (var response in responses)
        {
            response.Dispose();
        }

        statuses.Count(status => status == HttpStatusCode.OK).ShouldBe(1);
        statuses.Count(status => status == HttpStatusCode.PreconditionFailed).ShouldBe(3);
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(2, "one new version, no partial write of the losers");
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND version = 2")
            .ShouldBe(1);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_saved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task ApproveFactSheet_ThreeParallelApprovalsOfOneDraft_OneWins_AndExactlyOneRowIsApproved()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 3 ? [first] : [second]));
        var etag = FactSheetHarness.Etag(sheet.Id);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => FactSheetHarness.ApproveAsync(
            client,
            world.EpisodeId,
            etag)));

        var statuses = responses.Select(response => response.StatusCode).ToArray();
        foreach (var response in responses)
        {
            response.Dispose();
        }

        statuses.Count(status => status == HttpStatusCode.OK).ShouldBe(1);
        statuses.Count(status => status == HttpStatusCode.PreconditionFailed).ShouldBe(2);
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Approved).ShouldBe(1);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_approved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.fact_sheet_approved' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task ApproveFactSheet_RacingASecondPut_NeverAnswers500_AndTheInvariantsHold()
    {
        // The two-tab scenario at once: a draft B is approved while a second tab saves its own edit of B. Either
        // order is legal (the save lands and B is approved, or the save moves the current row and the approval
        // loses on its stale tag), but neither may 500 and the episode must end with exactly one approved row.
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var citations = (Func<int, IReadOnlyList<Guid>>)(number => number <= 3 ? [first] : [second]);
        var previous = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, citations),
            FactSheetStatus.Approved,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);
        var draft = await FactSheetHarness.SeedSheetAsync(Database, world, 2, Cites(5, citations, "Corrected fact"));
        var etag = FactSheetHarness.Etag(draft.Id);

        using var factory = FactSheetHarness.Factory(Database);
        using var approveClient = factory.ClientFor(world.UserId);
        using var editClient = factory.ClientFor(world.UserId);

        var responses = await Task.WhenAll(
            FactSheetHarness.ApproveAsync(approveClient, world.EpisodeId, etag),
            FactSheetHarness.PutAsync(
                editClient,
                world.EpisodeId,
                etag,
                FactSheetHarness.Body(
                    [
                        .. Enumerable.Range(1, 5).Select(number => FactSheetHarness.Fact(
                            $"f{number}",
                            $"Edited fact {number}.",
                            citations(number))),
                    ])));
        using var approve = responses[0];
        using var edit = responses[1];

        new[] { HttpStatusCode.OK, HttpStatusCode.PreconditionFailed }.ShouldContain(
            approve.StatusCode,
            $"the approval answers 200 or 412, never {approve.StatusCode}");
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Approved).ShouldBe(1);
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Superseded)
            .ShouldBe(approve.StatusCode == HttpStatusCode.OK ? 1 : 0);
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{previous.Id}'")
            .ShouldBe(approve.StatusCode == HttpStatusCode.OK ? "2" : "1");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(3, "the losing approval writes no row");
        SqlProbe.ScalarText(
                Database,
                $"SELECT status::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' ORDER BY version DESC LIMIT 1")
            .ShouldBe("0", "the highest version is always the editable draft");
    }

    [Fact]
    public async Task ApproveFactSheet_RacingAConcurrentApprover_MapsTheUniqueViolationTo412()
    {
        // The partial unique index is the real arbiter of "at most one approved row per episode" (plan §3
        // invariant 4). This test commits a competing approved row from a second connection just before the
        // handler's own approval flushes, so the UPDATE loses on ux_fact_sheets_episode_approved (23505) and the
        // unit of work has to answer 412 fact_sheet_conflict instead of surfacing a 500.
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 3 ? [first] : [second]));
        var competing = new ConcurrentApproverInterceptor(Database, world);

        using var factory = new ApiFactory(Database, new FakeClock(FactSheetHarness.Start))
            .ConfigureServices(services => services.AddDbContext<AppDbContext>(
                (_, options) => options.AddInterceptors(competing)));
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        competing.Inserts.ShouldBe(1, "the competing approval must land before this handler's own update flushes");
        await FactSheetHarness.AssertProblemAsync(response, 412, "fact_sheet_conflict");
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Approved).ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{sheet.Id}'")
            .ShouldBe("0", "the refused approval leaves its draft unturned");
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_approved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(0, "a refused approval commits no outbox row");
    }

    [Fact]
    public async Task PutFactSheet_RacingAGenerateJobVersion_MapsTheUniqueViolationTo412()
    {
        // A generate run can commit a new version of the same episode while the user edits the current one: the
        // insert runs on a second connection (the episode lock is FOR NO KEY UPDATE, so it does not exclude a
        // foreign-key read). The edit then loses on ux_fact_sheets_episode_version (23505) and must answer 412
        // fact_sheet_conflict, never a 500, and never a partial write of its own version.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);
        var competing = new JobVersionInterceptor(Database, world, source);

        using var factory = new ApiFactory(Database, new FakeClock(FactSheetHarness.Start))
            .ConfigureServices(services => services.AddDbContext<AppDbContext>(
                (_, options) => options.AddInterceptors(competing)));
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source])));

        competing.Inserts.ShouldBe(1);
        await FactSheetHarness.AssertProblemAsync(response, 412, "fact_sheet_conflict");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(2, "the competing job version is the only new row");
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND version = 2")
            .ShouldBe(1, "exactly one row may hold version 2 (ux_fact_sheets_episode_version)");
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{sheet.Id}'").ShouldBe("0");
    }

    /// <summary>
    /// Commits a competing <em>approved</em> version of the episode from a second connection the first time the
    /// request's own unit of work flushes a fact-sheet change — the state a concurrent approval leaves behind.
    /// </summary>
    private sealed class ConcurrentApproverInterceptor(TestDatabase database, World world) : SaveChangesInterceptor
    {
        /// <summary>How many competing rows the hook wrote; exactly one per test.</summary>
        public int Inserts { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);

            if (Inserts > 0 || !FlushesItsSheet(eventData))
            {
                return result;
            }

            Inserts++;
            await new FactSheetBuilder(database, new TestCurrentUser { Id = world.UserId }, new FakeClock(FactSheetHarness.Start))
                .CreateVersionAsync(
                    world.UserId,
                    world.EpisodeId,
                    99,
                    [],
                    new Dictionary<string, Guid>(StringComparer.Ordinal),
                    maxFactId: 0,
                    status: FactSheetStatus.Approved,
                    approvedBy: world.UserId,
                    approvedAt: FactSheetHarness.Start,
                    ct: cancellationToken)
                .ConfigureAwait(false);

            return result;
        }
    }

    /// <summary>
    /// Commits a competing draft version of the episode from a second connection the first time the request's own
    /// unit of work flushes a fact-sheet change — the state the generate job leaves behind (02.5.3-T2).
    /// </summary>
    private sealed class JobVersionInterceptor(TestDatabase database, World world, Guid sourceId) : SaveChangesInterceptor
    {
        /// <summary>How many competing rows the hook wrote; exactly one per test.</summary>
        public int Inserts { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);

            if (Inserts > 0 || !FlushesItsSheet(eventData))
            {
                return result;
            }

            Inserts++;
            await new FactSheetBuilder(database, new TestCurrentUser { Id = world.UserId }, new FakeClock(FactSheetHarness.Start))
                .CreateVersionAsync(
                    world.UserId,
                    world.EpisodeId,
                    2,
                    [new FactItem("f9", "A fact the run extracted.", [sourceId], ["s1"], null, FactConfidence.High, FactOrigin.Model)],
                    new Dictionary<string, Guid>(StringComparer.Ordinal) { ["s1"] = sourceId },
                    maxFactId: 9,
                    ct: cancellationToken)
                .ConfigureAwait(false);

            return result;
        }
    }

    /// <summary>Whether this flush is the request's own fact-sheet change.</summary>
    private static bool FlushesItsSheet(DbContextEventData eventData) =>
        eventData.Context?.ChangeTracker.Entries<FactSheet>()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified) == true;

    /// <summary>Builds <paramref name="count"/> facts numbered <c>f1…fN</c>, each citing what <paramref name="citations"/> picks.</summary>
    private static FactSeed[] Cites(
        int count,
        Func<int, IReadOnlyList<Guid>> citations,
        string statementPrefix = "Fact number") =>
        [
            .. Enumerable.Range(1, count).Select(number => FactSheetHarness.Seed(
                $"f{number}",
                $"{statementPrefix} {number}.",
                citations(number))),
        ];
}
