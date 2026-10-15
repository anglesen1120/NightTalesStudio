using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Keywords;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// <c>NameCorpusProvider</c> (03 README D11, 03.2.2-T1): the corpus of one episode is its fetched <c>url</c>
/// source titles plus the statements of its approved fact sheet, and nothing else — a draft sheet, a note, a
/// pending source, another tenant's rows and a fiction topic all leave <see cref="NameCorpus.Empty"/>, and
/// <c>Images:Linter:NameCorpusEnabled = false</c> stops the read entirely.
/// </summary>
/// <remarks>
/// The host composes the production <c>AddUsageMetering</c> registration over the class database through
/// <see cref="RenderNodeTestHost"/>, so a registration this task forgets fails the test instead of hiding
/// behind a hand-built container; the actor is the documented <see cref="TestCurrentUser"/> seam, which is
/// what makes the ambient tenant filter (09 README D10) observable. A class database is shared by every test
/// here, so each test answers with keywords about the episode it seeded itself.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class NameCorpusProviderTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A sentence-case Vietnamese headline whose long tokens stay mostly lower case, so the D11 Title Case
    /// guard keeps it; its run is <c>Nguyen Van An</c> (and <c>Da Lat</c>).
    /// </summary>
    private const string NameTitle =
        "Cảnh sát điều tra vụ án Nguyễn Văn An tại thành phố Đà Lạt trong nhiều tháng";

    /// <summary>A sentence-case fact statement whose run is <c>Dang Thi Hong</c>.</summary>
    private const string NameStatement =
        "Đặng Thị Hồng khai rằng cô đã gặp nạn nhân trước khi vụ án xảy ra.";

    /// <summary>A statement with no run of two capitalised tokens: it can never contribute a name.</summary>
    private const string HarmlessStatement = "The victim lived in a small coastal town.";

    /// <summary>A source title with no run of two capitalised tokens.</summary>
    private const string HarmlessTitle = "Local report on the incident";

    /// <summary>The D11 rollback switch and its configuration path.</summary>
    private const string NameCorpusEnabledKey = "Images:Linter:NameCorpusEnabled";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Provider_TrueCrime_LoadsTitlesAndApprovedFacts()
    {
        var (userId, episodeId) = await SeedEpisodeAsync(Topic.TrueCrime);
        var sourceId = await AddFetchedUrlAsync(userId, episodeId, NameTitle);
        await SeedSheetAsync(userId, episodeId, version: 1, NameStatement, sourceId, FactSheetStatus.Approved);

        using var host = BuildHost();
        var corpus = await LoadAsync(host, userId, episodeId);

        corpus.Matches("portrait of van an at the market").ShouldBeTrue("the fetched title names Nguyễn Văn An");
        corpus.Matches("a close up of dang thi hong").ShouldBeTrue("the approved fact names Đặng Thị Hồng");
        corpus.Matches("a pine forest in da lat").ShouldBeTrue("a place name of the title is caught too (G4 [DECISION])");
        corpus.Matches("mountain town at night").ShouldBeFalse("no run of the corpus appears in that keyword");
    }

    [Fact]
    public async Task Provider_DraftFactSheet_Ignored()
    {
        var (userId, episodeId) = await SeedEpisodeAsync(Topic.TrueCrime);
        var sourceId = await AddFetchedUrlAsync(userId, episodeId, HarmlessTitle);
        await SeedSheetAsync(userId, episodeId, version: 1, HarmlessStatement, sourceId, FactSheetStatus.Approved);
        // The newer version is a draft: only the approved row is a name source (D11).
        await SeedSheetAsync(userId, episodeId, version: 2, NameStatement, sourceId, FactSheetStatus.Draft);

        using var host = BuildHost();
        var corpus = await LoadAsync(host, userId, episodeId);

        corpus.Matches("a close up of dang thi hong").ShouldBeFalse("a draft fact sheet is not a name source");
    }

    [Fact]
    public async Task Provider_FictionTopic_ReturnsEmpty()
    {
        var (userId, episodeId) = await SeedEpisodeAsync(Topic.Ghost);
        var sourceId = await AddFetchedUrlAsync(userId, episodeId, NameTitle);
        await SeedSheetAsync(userId, episodeId, version: 1, NameStatement, sourceId, FactSheetStatus.Approved);

        using var host = BuildHost();
        var corpus = await LoadAsync(host, userId, episodeId);

        corpus.Matches("portrait of van an at the market").ShouldBeFalse("a fiction series has no real-person corpus");
        corpus.Matches("a close up of dang thi hong").ShouldBeFalse("the approved fact is not read for fiction");
    }

    [Fact]
    public async Task Provider_FlagOff_ReturnsEmpty()
    {
        var (userId, episodeId) = await SeedEpisodeAsync(Topic.TrueCrime);
        var sourceId = await AddFetchedUrlAsync(userId, episodeId, NameTitle);
        await SeedSheetAsync(userId, episodeId, version: 1, NameStatement, sourceId, FactSheetStatus.Approved);

        using var host = BuildHost(nameCorpusEnabled: false);
        var corpus = await LoadAsync(host, userId, episodeId);

        corpus.Matches("portrait of van an at the market").ShouldBeFalse("D11's rollback stops the name rule");
        corpus.Matches("a close up of dang thi hong").ShouldBeFalse("D11's rollback stops the name rule");
    }

    [Fact]
    public async Task Provider_PendingSourceAndNote_Ignored()
    {
        var (userId, episodeId) = await SeedEpisodeAsync(Topic.TrueCrime);
        await AddPendingUrlAsync(userId, episodeId);
        await AddFetchedNoteAsync(userId, episodeId, $"Ghi chú: Nguyễn Văn An {NameStatement}");

        using var host = BuildHost();
        var corpus = await LoadAsync(host, userId, episodeId);

        corpus.Matches("portrait of van an at the market")
            .ShouldBeFalse("a note has no title and a pending source has none either: D11 reads fetched url titles");
    }

    [Fact]
    public async Task Provider_OtherUsersFacts_NotLoaded()
    {
        using var host = BuildHost();

        // The caller's own episode carries no name, so anything the corpus answers must come from a leak.
        var (mine, myEpisodeId) = await SeedEpisodeAsync(Topic.TrueCrime);
        await AddFetchedUrlAsync(mine, myEpisodeId, HarmlessTitle);

        var (theirs, theirEpisodeId) = await SeedEpisodeAsync(Topic.TrueCrime);
        var theirSourceId = await AddFetchedUrlAsync(theirs, theirEpisodeId, NameTitle);
        await SeedSheetAsync(theirs, theirEpisodeId, version: 1, NameStatement, theirSourceId, FactSheetStatus.Approved);

        var asMe = await LoadAsync(host, mine, myEpisodeId);
        asMe.Matches("portrait of van an at the market")
            .ShouldBeFalse("another user's source title must not reach this episode's corpus");
        asMe.Matches("a close up of dang thi hong")
            .ShouldBeFalse("another user's approved fact must not reach this episode's corpus");

        var theirEpisodeAsMe = await LoadAsync(host, mine, theirEpisodeId);
        theirEpisodeAsMe.Matches("portrait of van an at the market")
            .ShouldBeFalse("another user's episode is invisible, so its corpus is empty");

        // The positive control: the rows really do carry the name, they are just not visible to the caller.
        var asThem = await LoadAsync(host, theirs, theirEpisodeId);
        asThem.Matches("portrait of van an at the market").ShouldBeTrue("the owner reads their own episode");
    }

    /// <summary>Builds the production <c>AddUsageMetering</c> graph over the class database (see the class remarks).</summary>
    /// <param name="nameCorpusEnabled">Whether <c>Images:Linter:NameCorpusEnabled</c> is set at all; setting it to false drives D11's rollback.</param>
    /// <returns>The host; the caller disposes it.</returns>
    private RenderNodeTestHost BuildHost(bool nameCorpusEnabled = true)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"ConnectionStrings:{PersistenceOptions.ConnectionStringName}"] = Database.ConnectionString,
        };

        if (!nameCorpusEnabled)
        {
            settings[NameCorpusEnabledKey] = "false";
        }

        return RenderNodeTestHost.Build(
            Database,
            new FakeClock(Start),
            settings,
            configure: services => services.AddUsageMetering());
    }

    /// <summary>Resolves the provider in a fresh scope whose actor is <paramref name="callerId"/>.</summary>
    private static async Task<NameCorpus> LoadAsync(RenderNodeTestHost host, Guid callerId, Guid episodeId)
    {
        // The read model captures the actor at context construction, so the actor is set before the scope
        // resolves IReadDb (09 README D10).
        ((TestCurrentUser)host.Services.GetRequiredService<ICurrentUser>()).Id = callerId;

        using var scope = host.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<INameCorpusProvider>().LoadAsync(episodeId, Ct);
    }

    /// <summary>Seeds one owner, one series of <paramref name="topic"/> and one episode.</summary>
    private async Task<(Guid UserId, Guid EpisodeId)> SeedEpisodeAsync(Topic topic)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor, new FakeClock(Start))
            .CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, ct: Ct);

        return (user.Id, episode.Id);
    }

    /// <summary>Adds one <c>url</c> source of the episode, already <c>Fetched</c> with <paramref name="title"/>.</summary>
    private async Task<Guid> AddFetchedUrlAsync(Guid userId, Guid episodeId, string title)
    {
        var (source, address) = BuildUrl(userId, episodeId);
        source.MarkFetched(address, title, "text/html", new string('a', 64), $"research/{Guid.CreateVersion7():N}.txt", 120, Start);

        return await SaveSourceAsync(userId, source);
    }

    /// <summary>Adds one <c>url</c> source of the episode that was never fetched, so it has no title.</summary>
    private async Task<Guid> AddPendingUrlAsync(Guid userId, Guid episodeId)
    {
        var (source, _) = BuildUrl(userId, episodeId);

        return await SaveSourceAsync(userId, source);
    }

    /// <summary>Adds one fetched <c>user_note</c> source of the episode carrying <paramref name="text"/>.</summary>
    private async Task<Guid> AddFetchedNoteAsync(Guid userId, Guid episodeId, string text)
    {
        var source = StorySource.CreateNote(Guid.CreateVersion7(), userId, episodeId, text, Start);
        source.IsError.ShouldBeFalse();

        return await SaveSourceAsync(userId, source.Value);
    }

    /// <summary>Seeds one fact-sheet version of the episode carrying <paramref name="statement"/> and citing <paramref name="sourceId"/>.</summary>
    private async Task SeedSheetAsync(
        Guid userId,
        Guid episodeId,
        int version,
        string statement,
        Guid sourceId,
        FactSheetStatus status)
    {
        var facts = new List<FactItem>
        {
            new("f1", statement, [sourceId], ["s1"], null, FactConfidence.High, FactOrigin.Model),
        };
        var aliases = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["s1"] = sourceId };

        await new FactSheetBuilder(Database, new TestCurrentUser { Id = userId }, new FakeClock(Start))
            .CreateVersionAsync(
                userId,
                episodeId,
                version,
                facts,
                aliases,
                maxFactId: 1,
                status: status,
                approvedBy: status == FactSheetStatus.Approved ? userId : null,
                approvedAt: status == FactSheetStatus.Approved ? Start : null,
                ct: Ct);
    }

    private static (StorySource Source, string Address) BuildUrl(Guid userId, Guid episodeId)
    {
        var address = $"https://example.com/article-{Guid.CreateVersion7():N}";
        var parsed = SourceUrl.Parse(address, new FakePublicSuffixList());
        parsed.IsError.ShouldBeFalse();

        var source = StorySource.CreateUrl(Guid.CreateVersion7(), userId, episodeId, parsed.Value, Start);
        source.IsError.ShouldBeFalse();

        return (source.Value, address);
    }

    private async Task<Guid> SaveSourceAsync(Guid userId, StorySource source)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        context.StorySources.Add(source);
        await context.SaveChangesAsync(Ct);

        return source.Id;
    }
}
