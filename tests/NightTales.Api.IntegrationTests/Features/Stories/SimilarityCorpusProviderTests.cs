using ErrorOr;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Similarity;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Stories;

/// <summary>Real PostgreSQL coverage of filtering, ownership, bounded sequential reads and current-version joins.</summary>
[Trait("Category", "Integration")]
public sealed class SimilarityCorpusProviderTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContextToken.Value;

    private async Task<(Guid UserId, Guid SeriesId, Guid EpisodeId)> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor, new FakeClock(Start)).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start)).CreateAsync(user.Id, series.Id, ct: Ct);
        return (user.Id, series.Id, episode.Id);
    }

    private Guid InsertSource(Guid userId, Guid episodeId, string status = "fetched", DateTimeOffset? at = null, bool hasKey = true)
    {
        var id = Guid.CreateVersion7();
        var key = hasKey ? $"'{id}'" : "NULL";
        SqlProbe.Execute(Database,
            "INSERT INTO story_sources (id,user_id,episode_id,kind,url,normalized_url,registrable_domain,status,extracted_text_key,created_at) "
            + $"VALUES ('{id}','{userId}','{episodeId}','url','https://example.com/{id}','https://example.com/{id}',"
            + $"'example.com','{status}',{key},'{at ?? Start:O}')");
        return id;
    }

    [Fact]
    public async Task CorpusProvider_ExcludesUserNoteAndNonFetchedSources()
    {
        var world = await SeedAsync();
        var fetched = InsertSource(world.UserId, world.EpisodeId);
        InsertSource(world.UserId, world.EpisodeId, "pending");
        InsertSource(world.UserId, world.EpisodeId, "failed");
        InsertSource(world.UserId, world.EpisodeId, "rejected");
        InsertSource(world.UserId, world.EpisodeId, hasKey: false);
        SqlProbe.Execute(Database,
            "INSERT INTO story_sources (id,user_id,episode_id,kind,note_text,status,extracted_text_key,created_at) "
            + $"VALUES (gen_random_uuid(),'{world.UserId}','{world.EpisodeId}','user_note','private note','fetched','note-key',now())");
        var store = new FakeTextStore();
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var corpus = await new SimilarityCorpusProvider(db, store, Options.Create(new SimilaritySettings()))
            .LoadAsync(world.EpisodeId, Language.Vi, Ct);
        corpus.ShouldHaveSingleItem().SourceId.ShouldBe(fetched);
        store.Keys.ShouldHaveSingleItem().ShouldBe(fetched.ToString());
    }

    [Fact]
    public async Task CorpusProvider_TruncatesLongText()
    {
        var world = await SeedAsync();
        InsertSource(world.UserId, world.EpisodeId);
        var settings = new SimilaritySettings();
        var store = new FakeTextStore { Text = new string('a', 400_000) };
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var corpus = await new SimilarityCorpusProvider(db, store, Options.Create(settings)).LoadAsync(world.EpisodeId, Language.En, Ct);
        corpus.ShouldHaveSingleItem().Text.ShouldBe(new string('a', settings.Corpus.MaxSourceTextChars));
    }

    [Fact]
    public async Task CorpusProvider_TruncationDoesNotSplitSurrogatePair()
    {
        var world = await SeedAsync();
        InsertSource(world.UserId, world.EpisodeId);
        var settings = new SimilaritySettings();
        settings.Corpus.MaxSourceTextChars = 3;
        var store = new FakeTextStore { Text = "ab\U0001F600c" };
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var corpus = await new SimilarityCorpusProvider(db, store, Options.Create(settings)).LoadAsync(world.EpisodeId, Language.En, Ct);
        corpus.ShouldHaveSingleItem().Text.ShouldBe("ab");
    }

    [Fact]
    public async Task CorpusProvider_NoSources_ReturnsEmpty()
    {
        var world = await SeedAsync();
        var store = new FakeTextStore();
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        (await new SimilarityCorpusProvider(db, store, Options.Create(new SimilaritySettings()))
            .LoadAsync(world.EpisodeId, Language.En, Ct)).ShouldBeEmpty();
        store.Keys.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public async Task CorpusProvider_WhitespaceRead_IsSkipped(string text)
    {
        var world = await SeedAsync();
        InsertSource(world.UserId, world.EpisodeId);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        (await new SimilarityCorpusProvider(db, new FakeTextStore { Text = text }, Options.Create(new SimilaritySettings()))
            .LoadAsync(world.EpisodeId, Language.En, Ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("service_unavailable")]
    public async Task CorpusProvider_StorageError_ThrowsContentFreeUnavailable(string code)
    {
        var world = await SeedAsync();
        var source = InsertSource(world.UserId, world.EpisodeId);
        var store = new FakeTextStore { ErrorCode = code };
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var provider = new SimilarityCorpusProvider(db, store, Options.Create(new SimilaritySettings()));
        var error = await Should.ThrowAsync<SimilarityCorpusUnavailableException>(async () =>
            await provider.LoadAsync(world.EpisodeId, Language.En, Ct));
        error.Reason.ShouldBe(SimilarityUnavailableReason.StorageError);
        error.Message.ShouldBe("The similarity corpus is unavailable.");
        error.Message.ShouldNotContain(source.ToString());
    }

    [Fact]
    public async Task CorpusProvider_OrdersByCreatedAtThenId_AndLimitsStorageReads()
    {
        var world = await SeedAsync();
        InsertSource(world.UserId, world.EpisodeId, at: Start.AddMinutes(1));
        var firstTie = InsertSource(world.UserId, world.EpisodeId, at: Start);
        var secondTie = InsertSource(world.UserId, world.EpisodeId, at: Start);
        var oldest = InsertSource(world.UserId, world.EpisodeId, at: Start.AddMinutes(-1));
        var expectedTie = firstTie.CompareTo(secondTie) < 0 ? firstTie : secondTie;
        var settings = new SimilaritySettings();
        settings.Corpus.MaxSourceTexts = 2;
        var store = new FakeTextStore();
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var corpus = await new SimilarityCorpusProvider(db, store, Options.Create(settings)).LoadAsync(world.EpisodeId, Language.En, Ct);
        corpus.Select(text => text.SourceId).ShouldBe(new[] { oldest, expectedTie });
        store.Keys.ShouldBe(new[] { oldest.ToString(), expectedTie.ToString() });
        store.MaxConcurrentReads.ShouldBe(1);
    }

    [Fact]
    public async Task CorpusProvider_AnotherUserOnEpisodePath_IsNeverReturned()
    {
        var owner = await SeedAsync();
        var other = await SeedAsync();
        InsertSource(other.UserId, other.EpisodeId);
        // The composite FK is the first control; the provider also joins source and episode owners.
        var rejected = Should.Throw<PostgresException>(() => InsertSource(other.UserId, owner.EpisodeId));
        rejected.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        var store = new FakeTextStore();
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = owner.UserId });
        var provider = new SimilarityCorpusProvider(db, store, Options.Create(new SimilaritySettings()));
        (await provider.LoadAsync(owner.EpisodeId, Language.En, Ct)).ShouldBeEmpty();
        (await provider.LoadAsync(other.EpisodeId, Language.En, Ct)).ShouldBeEmpty();
        store.Keys.ShouldBeEmpty();
    }

    [Fact]
    public async Task CorpusProvider_SystemOwnerScope_RetainsTenantIsolation()
    {
        var owner = await SeedAsync();
        var other = await SeedAsync();
        var source = InsertSource(owner.UserId, owner.EpisodeId);
        InsertSource(other.UserId, other.EpisodeId);
        var actor = new ScopedCurrentUser();
        using var ownerScope = actor.For(owner.UserId);
        await using var db = Database.CreateAppDbContext(actor);
        var provider = new SimilarityCorpusProvider(db, new FakeTextStore(), Options.Create(new SimilaritySettings()));
        (await provider.LoadAsync(owner.EpisodeId, Language.En, Ct)).ShouldHaveSingleItem().SourceId.ShouldBe(source);
        (await provider.LoadAsync(other.EpisodeId, Language.En, Ct)).ShouldBeEmpty();
    }

    private void InsertStory(Guid userId, Guid episodeId, int version, string body, bool makeCurrent)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(Database,
            "INSERT INTO story_versions (id,user_id,episode_id,version,source,title,body,keywords,word_count,content,created_at) "
            + $"VALUES ('{id}','{userId}','{episodeId}',{version},'generated','title','{body}','{{}}',1,'{{}}'::jsonb,now())");
        if (makeCurrent)
        {
            SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id='{id}' WHERE id='{episodeId}'");
        }
    }

    [Fact]
    public async Task PastEpisodes_ExcludesCurrentEpisode_AndCapsAtMax()
    {
        var world = await SeedAsync();
        var builder = new EpisodeBuilder(Database, new TestCurrentUser());
        var older = await builder.CreateAsync(world.UserId, world.SeriesId, createdAt: Start.AddMinutes(-1), ct: Ct);
        var newer = await builder.CreateAsync(world.UserId, world.SeriesId, createdAt: Start.AddMinutes(1), ct: Ct);
        var withoutCurrent = await builder.CreateAsync(world.UserId, world.SeriesId, createdAt: Start.AddMinutes(2), ct: Ct);
        InsertStory(world.UserId, world.EpisodeId, 1, "excluded", true);
        InsertStory(world.UserId, older.Id, 1, "older", true);
        InsertStory(world.UserId, newer.Id, 1, "current body", true);
        InsertStory(world.UserId, newer.Id, 2, "not current body", false);
        InsertStory(world.UserId, withoutCurrent.Id, 1, "unattached body", false);
        var otherSeries = await new SeriesBuilder(Database, new TestCurrentUser()).CreateAsync(world.UserId, ct: Ct);
        var outside = await builder.CreateAsync(world.UserId, otherSeries.Id, createdAt: Start.AddMinutes(3), ct: Ct);
        InsertStory(world.UserId, outside.Id, 1, "different series", true);
        var settings = new SimilaritySettings();
        settings.Corpus.MaxPastEpisodes = 1;
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var provider = new SimilarityCorpusProvider(db, new FakeTextStore(), Options.Create(settings));
        var capped = await provider.GetPastEpisodesAsync(world.SeriesId, world.EpisodeId, int.MaxValue, Ct);
        capped.ShouldHaveSingleItem().ShouldBe(new CorpusText(newer.Id, "current body"));
        (await provider.GetPastEpisodesAsync(world.SeriesId, world.EpisodeId, 0, Ct)).ShouldBeEmpty();
        (await provider.GetPastEpisodesAsync(world.SeriesId, world.EpisodeId, -1, Ct)).ShouldBeEmpty();
        settings.Corpus.MaxPastEpisodes = 50;
        var all = await provider.GetPastEpisodesAsync(world.SeriesId, world.EpisodeId, 50, Ct);
        all.Select(text => text.SourceId).ShouldBe(new[] { newer.Id, older.Id });
    }

    [Fact]
    public async Task PastEpisodes_AnotherUsersSeries_ReturnsEmpty()
    {
        var owner = await SeedAsync();
        var other = await SeedAsync();
        InsertStory(other.UserId, other.EpisodeId, 1, "other owner body", true);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = owner.UserId });
        (await new SimilarityCorpusProvider(db, new FakeTextStore(), Options.Create(new SimilaritySettings()))
            .GetPastEpisodesAsync(other.SeriesId, Guid.Empty, 50, Ct)).ShouldBeEmpty();
    }

    [Theory]
    // BlockAbove must exceed WarnAbove; the calibrated WarnAbove is 0.02 (02.5.6-T2), so 0.01 is invalid.
    [InlineData("Similarity:BlockAbove", "0.01")]
    [InlineData("Similarity:N:vi", "1")]
    public void Startup_InvalidSimilarityConfiguration_FailsWithANamedError(string key, string value)
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start)).WithSetting(key, value);
        var failure = Should.Throw<Exception>(() => factory.CreateClient());
        var messages = new List<string>();
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        string.Join(" | ", messages).ShouldContain(key);
    }

    private sealed class FakeTextStore : IExtractedTextStore
    {
        private int _active;
        public string Text { get; init; } = "fetched source text";
        public string? ErrorCode { get; init; }
        public List<string> Keys { get; } = [];
        public int MaxConcurrentReads { get; private set; }
        public async Task<ErrorOr<string>> ReadAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Keys.Add(key);
            _active++;
            MaxConcurrentReads = Math.Max(MaxConcurrentReads, _active);
            try
            {
                await Task.Yield();
                return ErrorCode is null ? Text : Error.Failure(ErrorCode, "read unavailable");
            }
            finally
            {
                _active--;
            }
        }

        public Task<ErrorOr<string>> WriteAsync(Guid userId, Guid episodeId, Guid sourceId, string sha256,
            string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
