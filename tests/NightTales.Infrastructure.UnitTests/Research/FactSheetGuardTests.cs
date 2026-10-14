using System.Globalization;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>
/// The real <see cref="FactSheetGuard"/> (02.5.5-T1, 02 README D6/D8/D21/CR-24) against real PostgreSQL rows: a
/// fiction episode needs no sheet and pays no read, a true-crime episode may only run on the episode's
/// currently approved, non-empty sheet, and every fail-closed branch of the D-3 contract answers the same D8
/// 409 <c>fact_sheet_required</c>.
/// </summary>
/// <remarks>
/// The guard is Application-layer code, but its contract is a pair of tenant-filtered reads, so it is exercised
/// over the same real read model the Api and the Worker resolve (<see cref="TestDatabase.CreateReadDbContext"/>)
/// rather than over a queryable fake; the one test that must prove "no read at all" swaps in a read model that
/// throws on any query.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FactSheetGuardTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Topic.Ghost)]
    [InlineData(Topic.Spiritual)]
    public async Task FactSheetGuard_FictionTopic_ReturnsApprovedFactSheetNone(Topic topic)
    {
        var world = await SeedAsync(topic);
        var source = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "A fact of an earlier topic.", source.Id, "s1")],
            Aliases(("s1", source.Id)));

        // The read model throws on any query, so a fiction episode that started reading would fail here instead
        // of passing by accident.
        var guard = new FactSheetGuard(new NeverReadDb());

        var result = await guard.EnsureApprovedAsync(world.EpisodeId, topic, Ct);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeSameAs(ApprovedFactSheet.None, "a fiction episode needs no sheet and pays no read");
        (await CountAsync($"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND status = 1"))
            .ShouldBe(1, "the approved sheet is really there: the fiction path ignores it rather than finding none");
    }

    [Fact]
    public async Task FactSheetGuard_FictionTopicWithARealReadModel_IgnoresTheApprovedSheet()
    {
        var world = await SeedAsync(Topic.Ghost);
        var source = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "A fact of an earlier topic.", source.Id, "s1")],
            Aliases(("s1", source.Id)));

        await using var read = Database.CreateReadDbContext(new TestCurrentUser { Id = world.UserId });

        var result = await new FactSheetGuard(read).EnsureApprovedAsync(world.EpisodeId, Topic.Ghost, Ct);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeSameAs(
            ApprovedFactSheet.None,
            "the topic decides, not the rows: the sheet is visible to this scope and must still not be read");
    }

    [Fact]
    public async Task FactSheetGuard_TrueCrime_NoApprovedSheet_Returns409FactSheetRequired()
    {
        var world = await SeedAsync();

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        AssertRequired(result);
    }

    [Fact]
    public async Task FactSheetGuard_TrueCrime_EmptyApprovedSheet_Returns409FactSheetRequired()
    {
        var world = await SeedAsync();
        // An approved version without facts is not usable: there is nothing the writer could rest a story on.
        await ApprovedSheetAsync(world, world.EpisodeId, [], Aliases());

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        AssertRequired(result);
    }

    [Fact]
    public async Task FactSheetGuard_SupersededSheetIsNotApproved()
    {
        var world = await SeedAsync();
        var first = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        var second = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('b', 64));
        // A superseded row is a full, resolvable sheet — only its lifecycle state disqualifies it.
        await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "One.", first.Id, "s1"), CitedFact("f2", "Two.", second.Id, "s2")],
            Aliases(("s1", first.Id), ("s2", second.Id)),
            status: FactSheetStatus.Superseded);

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        AssertRequired(result);
    }

    [Fact]
    public async Task FactSheetGuard_DraftSheetIsNotApproved()
    {
        var world = await SeedAsync();
        var source = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "One.", source.Id, "s1")],
            Aliases(("s1", source.Id)),
            status: FactSheetStatus.Draft);

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        AssertRequired(result);
    }

    [Fact]
    public async Task FactSheetGuard_TrueCrime_ApprovedSheet_ReturnsFactsWithAliasesOnly_NoSourceIdsOrUrls()
    {
        var world = await SeedAsync();
        var first = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        var second = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('b', 64));
        var sheet = await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [
                CitedFact("f1", "The first approved fact.", first.Id, "s1"),
                CitedFact("f2", "The second approved fact.", second.Id, "s2"),
            ],
            Aliases(("s1", first.Id), ("s2", second.Id)));

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        result.IsError.ShouldBeFalse();
        result.Value.FactSheetId.ShouldBe(sheet.Id);
        result.Value.Version.ShouldBe(sheet.Version);
        // Field by field, not one record ShouldBe: FactForWriter is a positional record, so its
        // IReadOnlyList<string> SourceAliases member would be compared by reference.
        result.Value.Facts.Count.ShouldBe(2);
        result.Value.Facts[0].Id.ShouldBe("f1");
        result.Value.Facts[0].Statement.ShouldBe("The first approved fact.");
        result.Value.Facts[0].SourceAliases.ShouldBe(["s1"]);
        result.Value.Facts[1].Id.ShouldBe("f2");
        result.Value.Facts[1].Statement.ShouldBe("The second approved fact.");
        result.Value.Facts[1].SourceAliases.ShouldBe(["s2"]);
        result.Value.AliasToSourceId.Count.ShouldBe(2);
        result.Value.AliasToSourceId["s1"].ShouldBe(first.Id);
        result.Value.AliasToSourceId["s2"].ShouldBe(second.Id);

        // D6/F12: the writer's fact shape is the id, the statement and the aliases — nothing else exists on it,
        // so no source id, URL, title, confidence or overlap can reach the prompt through this type.
        var writerFactMembers = new[] { "Id", "SourceAliases", "Statement" };
        typeof(FactForWriter).GetProperties()
            .Select(property => property.Name)
            .ShouldBe(writerFactMembers, ignoreOrder: true);
        result.Value.Facts
            .SelectMany(fact => fact.SourceAliases)
            .Any(alias => Guid.TryParse(alias, out _))
            .ShouldBeFalse("the writer sees aliases, never backend ids");
    }

    [Fact]
    public async Task FactSheetGuard_AliasMapRebuilt_FromStoredAliasesAndIds()
    {
        var world = await SeedAsync();
        // The episode has a live source row, and the approved sheet records an id whose row no longer exists:
        // the map of the row is what resolves a citation (CR-4), never a re-derivation from the live table.
        var live = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        var deleted = Guid.CreateVersion7();
        var sheet = await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "A fact whose source was deleted.", deleted, "s1")],
            Aliases(("s1", deleted)));

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        result.IsError.ShouldBeFalse();
        result.Value.FactSheetId.ShouldBe(sheet.Id);
        result.Value.AliasToSourceId.Count.ShouldBe(1, "the stored map is copied as it is; live rows are not folded in");
        result.Value.AliasToSourceId["s1"].ShouldBe(deleted, "the id the sheet recorded wins over the live row");
        result.Value.AliasToSourceId.Values.ShouldNotContain(live.Id, "s1 must not be re-pointed at the newest live source");
        (await Database.QueryStringsAsync($"SELECT source_aliases::text FROM fact_sheets WHERE id = '{sheet.Id}'", Ct))
            .ShouldHaveSingleItem()
            .ShouldContain(deleted.ToString(), Case.Sensitive, "the returned map is the column's own document");
    }

    [Fact]
    public async Task FactSheetGuard_ApprovedSheetOfAnotherEpisode_IsNotReturned()
    {
        var world = await SeedAsync();
        var otherEpisode = (await new EpisodeBuilder(Database, new TestCurrentUser())
            .CreateAsync(world.UserId, world.SeriesId, ct: Ct)).Id;
        var source = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        var otherSource = await SeedFetchedSourceAsync(world, otherEpisode, new string('b', 64));
        var sheet = await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "Mine.", source.Id, "s1")],
            Aliases(("s1", source.Id)));
        await ApprovedSheetAsync(
            world,
            otherEpisode,
            [CitedFact("f1", "Another episode's.", otherSource.Id, "s1")],
            Aliases(("s1", otherSource.Id)));

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        result.IsError.ShouldBeFalse();
        result.Value.FactSheetId.ShouldBe(sheet.Id, "only the asked episode's approved row answers");
        result.Value.Facts.ShouldHaveSingleItem().Statement.ShouldBe("Mine.");
    }

    [Fact]
    public async Task FactSheetGuard_SheetOfAnotherUser_IsNotVisible_Returns409FactSheetRequired()
    {
        var stranger = await SeedAsync();
        var strangerSource = await SeedFetchedSourceAsync(stranger, stranger.EpisodeId, new string('a', 64));
        await ApprovedSheetAsync(
            stranger,
            stranger.EpisodeId,
            [
                CitedFact("f1", "One.", strangerSource.Id, "s1"),
                CitedFact("f2", "Two.", strangerSource.Id, "s2"),
            ],
            Aliases(("s1", strangerSource.Id), ("s2", strangerSource.Id)));
        var world = await SeedAsync();

        // The stranger's own scope finds the sheet, so the fixture is real…
        (await EnsureAsync(stranger, stranger.EpisodeId, Topic.TrueCrime)).IsError
            .ShouldBeFalse("the other tenant's sheet is approved and resolvable");

        // …while our scope sees no approved row for that episode at all: the tenant filter, not the row's state,
        // is what refuses it.
        var result = await EnsureAsync(world, stranger.EpisodeId, Topic.TrueCrime);

        AssertRequired(result);
    }

    [Fact]
    public async Task FactSheetGuard_FactCitingAnAliasMissingFromTheMap_Returns409FactSheetRequired()
    {
        var world = await SeedAsync();
        var source = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        var sheet = await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "A fact.", source.Id, "s1")],
            Aliases(("s1", source.Id)));

        // The domain factory forbids this shape, so the stored document is corrupted on purpose: the writer
        // would receive an alias nothing can translate, and the guard must refuse the sheet instead.
        await Database.ExecuteAsync($"UPDATE fact_sheets SET source_aliases = '{{}}'::jsonb WHERE id = '{sheet.Id}'", Ct);

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        AssertRequired(result);
    }

    [Fact]
    public async Task FactSheetGuard_TwoFactsWithTheSameId_Returns409FactSheetRequired()
    {
        var world = await SeedAsync();
        var source = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        var sheet = await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "A fact.", source.Id, "s1")],
            Aliases(("s1", source.Id)));

        // FactSheet.CreateVersion rejects a repeated fact id, so the stored document is corrupted on purpose by
        // duplicating its own canonical element: every citation still resolves, so the alias checks pass, and the
        // guard — the fail-closed read side (S2/F4) — must refuse the sheet rather than let the writer's and the
        // story page's id-keyed fact lists silently keep the last entry.
        var stored = (await Database.QueryStringsAsync($"SELECT facts::text FROM fact_sheets WHERE id = '{sheet.Id}'", Ct))
            .ShouldHaveSingleItem()
            .Trim();
        var element = stored[1..^1];
        await Database.ExecuteAsync(
            $"UPDATE fact_sheets SET facts = '[{element},{element}]'::jsonb WHERE id = '{sheet.Id}'",
            Ct);

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        AssertRequired(result);
    }

    [Fact]
    public async Task FactSheetGuard_AliasToASourceOfAnotherEpisode_Returns409FactSheetRequired()
    {
        var world = await SeedAsync();
        var otherEpisode = (await new EpisodeBuilder(Database, new TestCurrentUser())
            .CreateAsync(world.UserId, world.SeriesId, ct: Ct)).Id;
        var foreign = await SeedFetchedSourceAsync(world, otherEpisode, new string('b', 64));
        await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "A fact citing another episode's source.", foreign.Id, "s1")],
            Aliases(("s1", foreign.Id)));

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        AssertRequired(result);
    }

    [Fact]
    public async Task FactSheetGuard_AliasToAForeignTenantsSource_IsInvisibleAndCarriesNothingOfIt()
    {
        var stranger = await SeedAsync();
        var hidden = await SeedFetchedSourceAsync(stranger, stranger.EpisodeId, new string('c', 64));
        var world = await SeedAsync();
        await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "A fact.", hidden.Id, "s1")],
            Aliases(("s1", hidden.Id)));

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        // A row the tenant filter hides cannot be told apart from a deleted one, so it is not an error: the row
        // can never back a story of this tenant (the gate's F13 read and the fact-source builder cannot see it
        // either), and nothing of it — title, url, hash — can reach this result.
        result.IsError.ShouldBeFalse();
        result.Value.AliasToSourceId["s1"].ShouldBe(hidden.Id);

        await using var read = Database.CreateReadDbContext(new TestCurrentUser { Id = world.UserId });
        (await read.Query<StorySource>().CountAsync(source => source.Id == hidden.Id, Ct))
            .ShouldBe(0, "the other tenant's row stays invisible to this scope");
        (await CountAsync(
                $"SELECT count(*) FROM story_sources WHERE id = '{hidden.Id}' AND episode_id = '{stranger.EpisodeId}'"))
            .ShouldBe(1, "the row exists for its own tenant");
    }

    [Fact]
    public async Task FactSheetGuard_VisibleMapRowOfAnotherOwner_Returns409FactSheetRequired()
    {
        var stranger = await SeedAsync();
        var foreign = await SeedFetchedSourceAsync(stranger, stranger.EpisodeId, new string('c', 64));
        var world = await SeedAsync();
        await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "A fact.", foreign.Id, "s1")],
            Aliases(("s1", foreign.Id)));

        // The guard itself never widens a scope, so this read model widens one on purpose: a system-style read
        // that sees every row turns the invisible row above into a visible foreign one, which the guard must
        // refuse rather than hand over.
        await using var unfiltered = Database.CreateReadDbContext(new TestCurrentUser { Id = world.UserId });
        var result = await new FactSheetGuard(new UnfilteredReadDb(unfiltered))
            .EnsureApprovedAsync(world.EpisodeId, Topic.TrueCrime, Ct);

        AssertRequired(result);
    }

    [Fact]
    public async Task FactSheetGuard_DeletedCitedSource_StillSucceeds()
    {
        var world = await SeedAsync();
        var kept = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        var deleted = Guid.CreateVersion7();
        await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "A fact on a deleted source.", deleted, "s1"), CitedFact("f2", "A fact on a kept source.", kept.Id, "s2")],
            Aliases(("s1", deleted), ("s2", kept.Id)));

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        result.IsError.ShouldBeFalse("deleting a cited source after approval does not un-approve the sheet");
        result.Value.Facts.Count.ShouldBe(2);
        result.Value.AliasToSourceId["s1"].ShouldBe(deleted, "the builder skips and reports the missing row, not the guard");
    }

    [Fact]
    public async Task FactSheetGuard_EveryReturnedAliasIsAnEpisodeAndUserScopedSourceRow()
    {
        var world = await SeedAsync();
        var first = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        var second = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('b', 64));
        var sheet = await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "One.", first.Id, "s1"), CitedFact("f2", "Two.", second.Id, "s2")],
            Aliases(("s1", first.Id), ("s2", second.Id)));

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        result.IsError.ShouldBeFalse();
        result.Value.FactSheetId.ShouldBe(sheet.Id);
        result.Value.AliasToSourceId.Keys.ShouldBe(["s1", "s2"], ignoreOrder: true);

        // Every id the guard hands over is a story_sources row of *this* episode and *this* owner — the two
        // predicates the citation resolver and the F13 count both rest on.
        (await CountAsync(
                $"SELECT count(*) FROM story_sources WHERE episode_id = '{world.EpisodeId}' AND user_id = '{world.UserId}' "
                + $"AND id = ANY(ARRAY['{first.Id}','{second.Id}']::uuid[])"))
            .ShouldBe(2, "each alias resolves to an episode- and owner-scoped source row");
    }

    [Fact]
    public async Task FactSheetGuard_ReadFailure_PropagatesInsteadOfReturningNone()
    {
        var world = await SeedAsync();
        var failure = new InvalidOperationException("the read model is unavailable");

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            async () => await new FactSheetGuard(new ThrowingReadDb(failure))
                .EnsureApprovedAsync(world.EpisodeId, Topic.TrueCrime, Ct));

        thrown.ShouldBeSameAs(failure, "fail closed, never open: no catch-all may turn a read failure into no sheet");
    }

    /// <summary>The guard over a real read model scoped to <paramref name="world"/>'s owner (the tenant filter).</summary>
    private async Task<ErrorOr<ApprovedFactSheet>> EnsureAsync(World world, Guid episodeId, Topic topic)
    {
        await using var read = Database.CreateReadDbContext(new TestCurrentUser { Id = world.UserId });

        return await new FactSheetGuard(read).EnsureApprovedAsync(episodeId, topic, Ct);
    }

    private static void AssertRequired(ErrorOr<ApprovedFactSheet> result)
    {
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.FactSheetRequired);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict, "D8 answers fact_sheet_required with 409");
    }

    private static FactItem CitedFact(string id, string statement, Guid sourceId, string alias) =>
        new(id, statement, [sourceId], [alias], null, FactConfidence.High, FactOrigin.Model);

    private static Dictionary<string, Guid> Aliases(params (string Alias, Guid SourceId)[] pairs)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (alias, sourceId) in pairs)
        {
            map[alias] = sourceId;
        }

        return map;
    }

    /// <summary>Seeds a user, a series of <paramref name="topic"/> and one draft episode.</summary>
    private async Task<World> SeedAsync(Topic topic = Topic.TrueCrime)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary>
    /// Inserts one fetched <c>story_sources</c> row of <paramref name="episodeId"/>, the shape F13 counts (a
    /// distinct content hash and a kept fact's citation are what the gate reads).
    /// </summary>
    private async Task<StorySource> SeedFetchedSourceAsync(World world, Guid episodeId, string sha256)
    {
        var id = Guid.CreateVersion7();
        var url = SourceUrl.Parse($"https://news.example.com/{id:N}", new FakePublicSuffixList()).Value;
        var source = StorySource.CreateUrl(id, world.UserId, episodeId, url, Now).Value;
        source.MarkFetched(url.Value, "Article", "text/html", sha256, $"research/{id:N}", 4_000, Now);

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        db.StorySources.Add(source);
        await db.SaveChangesAsync(Ct);

        return source;
    }

    /// <summary>
    /// Inserts one <c>fact_sheets</c> row through the domain factory, so every stored invariant (ids, the map
    /// covering every citation, the content hash) holds; only the lifecycle state is set explicitly.
    /// </summary>
    private async Task<FactSheet> ApprovedSheetAsync(
        World world,
        Guid episodeId,
        IReadOnlyList<FactItem> facts,
        IReadOnlyDictionary<string, Guid> aliases,
        int version = 1,
        FactSheetStatus status = FactSheetStatus.Approved)
    {
        var maxFactId = facts.Count == 0
            ? 0
            : facts.Max(fact => int.Parse(fact.Id.AsSpan(1), CultureInfo.InvariantCulture));

        return await new FactSheetBuilder(Database, new TestCurrentUser { Id = world.UserId })
            .CreateVersionAsync(
                world.UserId,
                episodeId,
                version,
                facts,
                aliases,
                maxFactId: maxFactId,
                status: status,
                approvedBy: status == FactSheetStatus.Approved ? world.UserId : null,
                approvedAt: status == FactSheetStatus.Approved ? Now : null,
                ct: Ct);
    }

    /// <summary>
    /// Reads one <c>count(*)</c> as a number, through a connection that bypasses EF and the tenant filter. The
    /// probe is wrapped and cast to <c>text</c>, because that connection reads every first column as a string.
    /// </summary>
    private async Task<long> CountAsync(string sql) =>
        long.Parse(
            (await Database.QueryStringsAsync($"SELECT ({sql})::text", Ct)).Single(),
            CultureInfo.InvariantCulture);

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    /// <summary>A read model that refuses to be read: the fiction path must not open one at all.</summary>
    private sealed class NeverReadDb : IReadDb
    {
        public IQueryable<TEntity> Query<TEntity>()
            where TEntity : class =>
            throw new InvalidOperationException("A fiction episode must not read the fact sheet of its episode.");
    }

    /// <summary>A read model whose every query fails, for the "a read failure propagates" branch.</summary>
    private sealed class ThrowingReadDb(InvalidOperationException failure) : IReadDb
    {
        public IQueryable<TEntity> Query<TEntity>()
            where TEntity : class =>
            throw failure;
    }

    /// <summary>
    /// A system-style read model over real rows: it drops the D10 tenant filter, so a row another owner holds
    /// becomes visible. The guard never does this itself; the test does it to prove what the guard then refuses.
    /// </summary>
    private sealed class UnfilteredReadDb(ReadDbContext inner) : IReadDb
    {
        public IQueryable<TEntity> Query<TEntity>()
            where TEntity : class =>
            inner.Query<TEntity>().IgnoreQueryFilters();
    }
}
