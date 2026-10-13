using System.Collections;
using System.Linq.Expressions;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore.Query;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Similarity;
using NightTales.TestKit.Research;
using NightTales.TestKit.Stories;
using Shouldly;
using Xunit;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.UnitTests.Features.Stories.Approval;

/// <summary>
/// Independent QA boundary set for the real approval gate (02.3.3-T1, 02 README D8/D21/F12/F13): the edges
/// the shipped suite leaves open — attestation versions that are not byte-identical, the
/// <c>MinCitedSources</c> floor at <em>its own value</em> and one below, the strict <c>&gt;</c> of the
/// similarity thresholds, a stored draft the scorer cannot read, a re-computed true-crime score, and the
/// source scoping (another episode, a user note, a failed fetch). Hand-written fakes only, no mock library.
/// </summary>
public sealed class StoryApprovalGateQaTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(" ta.v1")]
    [InlineData("ta.v1 ")]
    [InlineData("TA.V1")]
    [InlineData("ta.v1\n")]
    public async Task QaTests_AttestationVersionNotByteIdentical_ReturnsAttestationRequired(string version)
    {
        var rig = new Rig();
        var (story, _) = rig.Ready(score: 0.0m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, story, new AttestationInput(true, version), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryAttestationRequired);
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public async Task QaTests_AttestationAcceptedWithNullVersion_ReturnsAttestationRequired()
    {
        var rig = new Rig();
        var (story, _) = rig.Ready(score: 0.0m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, story, new AttestationInput(true, null!), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryAttestationRequired);
    }

    [Fact]
    public async Task QaTests_MinCitedSourcesThree_TwoQualifying_ReturnsSourcesRequired()
    {
        var rig = new Rig();
        rig.Options.MinCitedSources = 3;
        var (story, _) = rig.Ready(cited: 2, fetched: 2, score: 0.0m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, story, new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StorySourcesRequired);
        result.FirstError.Description.ShouldContain("cites 2", Case.Sensitive, "the message names the count it found");
        result.FirstError.Description.ShouldNotContain("http", Case.Insensitive, "a message never carries a URL");
    }

    [Fact]
    public async Task QaTests_MinCitedSourcesThree_ThreeQualifying_Passes()
    {
        var rig = new Rig();
        rig.Options.MinCitedSources = 3;
        var (story, _) = rig.Ready(cited: 3, fetched: 3, score: 0.0m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, story, new AttestationInput(true, "ta.v1"), Ct);

        result.IsError.ShouldBeFalse();
        result.Value.AcceptedTextVersion.ShouldBe("ta.v1");
    }

    [Fact]
    public async Task QaTests_MinCitedSourcesOne_OneQualifying_Passes_ZeroFails()
    {
        var rig = new Rig();
        rig.Options.MinCitedSources = 1;

        var (one, _) = rig.Ready(cited: 1, fetched: 1, score: 0.0m);
        var accepted = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, one, new AttestationInput(true, "ta.v1"), Ct);
        accepted.IsError.ShouldBeFalse();

        var (zero, _) = rig.Ready(cited: 1, fetched: 0, score: 0.0m);
        var refused = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, zero, new AttestationInput(true, "ta.v1"), Ct);
        refused.FirstError.Code.ShouldBe(ErrorCodes.StorySourcesRequired);
    }

    /// <summary>
    /// The classifier uses strict <c>&gt;</c> (02.5.6): a score equal to <c>BlockAbove</c> is a warning at
    /// most, and a score equal to <c>WarnAbove</c> is a pass. Only a score above the block threshold refuses.
    /// </summary>
    /// <param name="score">The stored <c>similarity_score</c>.</param>
    /// <param name="blocked">Whether 3d must refuse before the attestation sub-check is reached.</param>
    [Theory]
    [InlineData(0.15, false)]
    [InlineData(0.1501, true)]
    [InlineData(0.05, false)]
    [InlineData(0.0501, false)]
    public async Task QaTests_StoredScoreAtTheThresholds_IsClassifiedStrictly(double score, bool blocked)
    {
        var rig = new Rig();
        var (story, _) = rig.Ready(score: (decimal)score);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, story, new AttestationInput(true, "ta.v1"), Ct);

        if (blocked)
        {
            result.FirstError.Code.ShouldBe(ErrorCodes.StoryTooSimilarToSource);
        }
        else
        {
            // Equal-to-the-threshold scores never block, so the attestation sub-check decides.
            result.IsError.ShouldBeFalse();
            result.Value.AcceptedTextVersion.ShouldBe("ta.v1");
        }
    }

    [Fact]
    public async Task QaTests_TrueCrimeNullScoreWithCorpus_RecomputesThenBlocks()
    {
        var rig = new Rig();
        var (story, _) = rig.Ready(cited: 2, fetched: 2, score: null);
        rig.Corpus.Texts = [new CorpusText(Guid.CreateVersion7(), rig.StoryText)];

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, story, new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryTooSimilarToSource);
        rig.Corpus.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task QaTests_UnreadableStoredDraftWithUrlSources_ApprovesWithoutAScore()
    {
        var rig = new Rig(Topic.Ghost);
        rig.ReadDb.Sources.Add(rig.Fetched(Guid.CreateVersion7(), Rig.HashA));

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, rig.Version(rawContent: "{ this is not a story document"), null, Ct);

        result.IsError.ShouldBeFalse("an unreadable draft yields no score and a null score never blocks");
        rig.Corpus.Calls.ShouldBe(0, "an unreadable draft is decided before the corpus is ever read");
    }

    [Fact]
    public async Task QaTests_SourceOfAnotherEpisode_IsNeverCounted()
    {
        var rig = new Rig();
        var mine = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();
        rig.ReadDb.Sources.Add(rig.Fetched(mine, Rig.HashA));
        rig.ReadDb.Sources.Add(rig.Fetched(foreign, Rig.HashB, Guid.CreateVersion7()));

        var sheetId = Guid.CreateVersion7();
        rig.FactSheets.ApprovedSheet = Sheet(sheetId, new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["s1"] = mine,
            ["s2"] = foreign,
        });

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, rig.Version(sheetId, 0.0m), new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StorySourcesRequired);
        result.FirstError.Description.ShouldContain("cites 1", Case.Sensitive, "another episode's row is out of scope");
    }

    /// <summary>
    /// A cited user note is a <c>fetched</c> source with a real content hash (02.5.1 T1), so only the gate's
    /// url-only read keeps it out of the F13 floor.
    /// </summary>
    [Fact]
    public async Task QaTests_CitedUserNoteSource_NeverCounts()
    {
        var rig = new Rig();
        var article = Guid.CreateVersion7();
        var note = Guid.CreateVersion7();
        rig.ReadDb.Sources.Add(rig.Fetched(article, Rig.HashA));
        rig.ReadDb.Sources.Add(rig.Note(note));

        var sheetId = Guid.CreateVersion7();
        rig.FactSheets.ApprovedSheet = Sheet(sheetId, new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["s1"] = article,
            ["s2"] = note,
        });

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, rig.Version(sheetId, 0.0m), new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StorySourcesRequired);
        result.FirstError.Description.ShouldContain("cites 1");
    }

    [Fact]
    public async Task QaTests_CitedFailedSource_NeverCounts()
    {
        var rig = new Rig();
        var fetched = Guid.CreateVersion7();
        var failed = Guid.CreateVersion7();
        rig.ReadDb.Sources.Add(rig.Fetched(fetched, Rig.HashA));
        rig.ReadDb.Sources.Add(rig.Failed(failed));

        var sheetId = Guid.CreateVersion7();
        rig.FactSheets.ApprovedSheet = Sheet(sheetId, new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["s1"] = fetched,
            ["s2"] = failed,
        });

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, rig.Version(sheetId, 0.0m), new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StorySourcesRequired);
        result.FirstError.Description.ShouldContain("cites 1");
    }

    [Fact]
    public async Task QaTests_TrueCrimeGuardSucceedsWithNoSheet_ReturnsFactSheetOutdated()
    {
        var rig = new Rig();
        rig.FactSheets.ApprovedSheet = ApprovedFactSheet.None;

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, rig.Version(similarityScore: 0.0m), new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryFactSheetOutdated);
    }

    [Fact]
    public async Task QaTests_TwoCitedSourcesOnOneDomain_DistinctHashes_Qualifies()
    {
        var rig = new Rig();
        var (story, _) = rig.Ready(cited: 2, fetched: 2, score: 0.0m);

        // Every rig row is on https://news.example.com: F13's domain diversity is advisory only.
        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, story, new AttestationInput(true, "ta.v1"), Ct);

        result.IsError.ShouldBeFalse();
        result.Value.AcceptedTextVersion.ShouldBe("ta.v1");
    }

    private static ApprovedFactSheet Sheet(Guid sheetId, Dictionary<string, Guid> aliases) =>
        new(
            sheetId,
            1,
            aliases.Keys.Select(key => new FactForWriter($"f{key}", $"Fact {key}", [key])).ToList(),
            aliases);

    /// <summary>One gate world, the shipped rig's shape over this suite's own fakes.</summary>
    private sealed class Rig
    {
        internal static readonly string HashA = new('a', 64);
        internal static readonly string HashB = new('b', 64);

        private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

        // Long enough for the scorer's token floor (vi: 10-shingles over >= 50 tokens).
        private static readonly string LongText =
            string.Join(' ', Enumerable.Repeat("ngôi nhà bên sông vẫn im lặng mỗi đêm", 12));

        private Guid UserId { get; } = Guid.CreateVersion7();

        internal SeriesAggregate Series { get; }

        internal Episode Episode { get; }

        internal FakeFactSheetGuard FactSheets { get; } = new();

        internal FakeReadDb ReadDb { get; } = new();

        internal FakeCorpus Corpus { get; } = new();

        internal StoryTrueCrimeOptions Options { get; } = new();

        internal SimilaritySettings Settings { get; } = new();

        internal StoryApprovalGate Gate { get; }

        internal Rig(Topic topic = Topic.TrueCrime)
        {
            Series = SeriesAggregate.Create(
                UserId,
                new SeriesDraft("Gate qa rig", topic, Language.Vi, default, "voice", Guid.CreateVersion7(), null),
                Now);
            Episode = Episode.Create(UserId, Series.Id, "The house by the river", "A story for review", LengthTier.Short, Now);
            Gate = new StoryApprovalGate(FactSheets, ReadDb, Corpus, Options, Settings);
        }

        internal StoryVersion Version(Guid? factSheetId = null, decimal? similarityScore = null, string? rawContent = null)
        {
            var draft = Draft();
            return StoryVersion.Create(UserId, Episode.Id, 1, StoryVersionSource.Generated, draft,
                rawContent ?? StoryContentJson.Write(draft, new JsonArray()), "model-x", "rel=1", null, null,
                factSheetId, null, similarityScore, Now);
        }

        /// <summary>
        /// An approved sheet citing <paramref name="cited"/> distinct sources of which <paramref name="fetched"/>
        /// exist as fetched url rows, plus the version pinned to that sheet.
        /// </summary>
        internal (StoryVersion Version, Guid SheetId) Ready(int cited = 2, int fetched = 2, decimal? score = null)
        {
            var sheetId = Guid.CreateVersion7();
            var aliases = new Dictionary<string, Guid>(StringComparer.Ordinal);
            for (var index = 0; index < cited; index++)
            {
                var sourceId = Guid.CreateVersion7();
                aliases[$"s{index + 1}"] = sourceId;
                if (index < fetched)
                {
                    ReadDb.Sources.Add(Fetched(sourceId, new string((char)('a' + index), 64)));
                }
            }

            FactSheets.ApprovedSheet = Sheet(sheetId, aliases);
            return (Version(sheetId, score), sheetId);
        }

        internal StorySource Fetched(Guid id, string sha256, Guid? episodeId = null)
        {
            var url = SourceUrl.Parse($"https://news.example.com/{id:N}", new FakePublicSuffixList()).Value;
            var source = StorySource.CreateUrl(id, UserId, episodeId ?? Episode.Id, url, Now).Value;
            source.MarkFetched(url.Value, "Article", "text/html", sha256, $"research/{id:N}", 4_000, Now);
            return source;
        }

        internal StorySource Note(Guid id) =>
            StorySource.CreateNote(id, UserId, Episode.Id, "The user's own recollection of that night.", Now).Value;

        internal StorySource Failed(Guid id)
        {
            var url = SourceUrl.Parse($"https://news.example.com/{id:N}", new FakePublicSuffixList()).Value;
            var source = StorySource.CreateUrl(id, UserId, Episode.Id, url, Now).Value;
            source.MarkFailed("source_unreachable", 500, Now);
            return source;
        }

        /// <summary>The stored draft plus the corpus text that must match it for a blocking recompute.</summary>
        internal string StoryText { get; } = LongText;

        private static StoryContentV1 Draft() =>
            new("The house by the river",
                new StorySegmentV1(LongText, ["old house by the river"], []),
                [new StorySegmentV1(LongText, ["quiet river at night"], [], "setup")],
                new StorySegmentV1(LongText, ["empty path in the fog"], []),
                "A quiet story about a house by the river and the night it stayed silent.",
                ["truyện ma", "tâm linh"],
                ["supernatural"],
                new StoryStructureV1("truyện ma tâm linh", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"));
    }

    /// <summary>The existing hand-written EF async-query fake pattern, over this suite's own rows.</summary>
    private sealed class FakeReadDb : IReadDb
    {
        internal List<StorySource> Sources { get; } = [];

        public IQueryable<TEntity> Query<TEntity>()
            where TEntity : class =>
            typeof(TEntity) == typeof(StorySource)
                ? (IQueryable<TEntity>)(object)new AsyncQuery<StorySource>(Sources.AsQueryable())
                : throw new InvalidOperationException($"Unexpected query for {typeof(TEntity).Name}.");
    }

    private sealed class FakeCorpus : ISimilarityCorpusProvider
    {
        internal int Calls { get; private set; }

        internal IReadOnlyList<CorpusText> Texts { get; set; } = [];

        public ValueTask<IReadOnlyList<CorpusText>> LoadAsync(Guid episodeId, Language lang, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(Texts);
        }

        public ValueTask<IReadOnlyList<CorpusText>> GetPastEpisodesAsync(Guid episodeId, Guid excludeEpisodeId, int max, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<CorpusText>>([]);
    }

    private sealed class AsyncQuery<T>(IQueryable<T> query)
        : IOrderedQueryable<T>, IAsyncEnumerable<T>, IAsyncQueryProvider
    {
        public Type ElementType => typeof(T);

        public Expression Expression => query.Expression;

        public IQueryProvider Provider => this;

        public IEnumerator<T> GetEnumerator() => query.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var item in query)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }

        public IQueryable CreateQuery(Expression expression) => new AsyncQuery<T>(query.Provider.CreateQuery<T>(expression));

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new AsyncQuery<TElement>(query.Provider.CreateQuery<TElement>(expression));

        public object? Execute(Expression expression) => query.Provider.Execute(expression);

        public TResult Execute<TResult>(Expression expression) => query.Provider.Execute<TResult>(expression);

        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gate only enumerates its source query.");
    }
}
