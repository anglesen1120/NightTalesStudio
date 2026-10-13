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
/// The real approval gate (02.3.3-T1): sub-checks 3a-3e in their fixed order, the SEC-1 pinned-data rule,
/// the fail-closed error paths and the D8 wire codes. Hand-written fakes only, no mocking library.
/// </summary>
public sealed class StoryApprovalGateTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task StoryApprovalGate_Fiction_SkipsEveryCheck()
    {
        var rig = new Rig(Topic.Ghost);
        rig.ReadDb.Sources.Add(rig.Fetched(Guid.CreateVersion7(), Rig.HashA));
        var version = rig.Version();

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, version, new AttestationInput(true, "ta.v1"), Ct);

        result.IsError.ShouldBeFalse();
        result.Value.AcceptedTextVersion.ShouldBeNull();
        rig.FactSheets.Calls.ShouldBe(0, "a fiction story without a pinned fact sheet needs no sheet");
    }

    [Fact]
    public async Task StoryApprovalGate_TrueCrimeFactSheetStale_ReturnsFactSheetOutdated()
    {
        var rig = new Rig();
        rig.Ready();
        var stale = rig.Version(factSheetId: Guid.CreateVersion7(), similarityScore: 0.0m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, stale, new AttestationInput(true, "ta.v1"), Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.StoryFactSheetOutdated);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task StoryApprovalGate_TrueCrimeWithoutPinnedFactSheet_ReturnsFactSheetOutdated()
    {
        var rig = new Rig();
        rig.Ready();

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, rig.Version(similarityScore: 0.0m), new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryFactSheetOutdated);
    }

    [Fact]
    public async Task StoryApprovalGate_TrueCrimeInsufficientSources_ReturnsSourcesRequired()
    {
        var rig = new Rig();
        var (version, _) = rig.Ready(cited: 2, fetched: 1, similarityScore: 0.0m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, version, new AttestationInput(true, "ta.v1"), Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.StorySourcesRequired);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        result.FirstError.Description.ShouldContain("cites 1", Case.Sensitive, "the message names the count it found");
        result.FirstError.Description.ShouldNotContain("http", Case.Insensitive, "a message never carries a URL");
    }

    [Fact]
    public async Task StoryApprovalGate_TrueCrimeSimilarityAboveBlock_ReturnsTooSimilar()
    {
        var rig = new Rig();
        var (version, _) = rig.Ready(similarityScore: 0.5m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, version, new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryTooSimilarToSource);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task StoryApprovalGate_SourcedFictionEpisode_SimilarityBlock_ReturnsBlock()
    {
        var rig = new Rig(Topic.Ghost);
        rig.ReadDb.Sources.Add(rig.Fetched(Guid.CreateVersion7(), Rig.HashA));

        // 02 §5.9: the similarity sub-check is not topic-scoped, so a sourced fiction story is blocked too.
        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, rig.Version(similarityScore: 0.5m), null, Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryTooSimilarToSource);
    }

    [Fact]
    public async Task StoryApprovalGate_FictionWithoutSources_SimilarityPass()
    {
        var rig = new Rig(Topic.Ghost);

        var result = await rig.Gate.ValidateAsync(rig.Episode, rig.Series, rig.Version(), null, Ct);

        result.IsError.ShouldBeFalse();
        result.Value.AcceptedTextVersion.ShouldBeNull();
        rig.Corpus.Calls.ShouldBe(0, "an episode without url sources is never scored");
    }

    [Fact]
    public async Task StoryApprovalGate_TrueCrimeSimilarityWarnVerdict_DoesNotBlock()
    {
        var rig = new Rig();

        // 0.10 sits at or above WarnAbove (0.05) and below BlockAbove (0.15): a hint, never a refusal.
        var (version, _) = rig.Ready(similarityScore: 0.10m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, version, new AttestationInput(true, "ta.v1"), Ct);

        result.IsError.ShouldBeFalse();
        result.Value.AcceptedTextVersion.ShouldBe(rig.Options.AttestationVersion);
    }

    [Fact]
    public async Task StoryApprovalGate_NullScoreWithUrlSources_RecomputesThenClassifies()
    {
        var rig = new Rig(Topic.Ghost);
        var sourceId = Guid.CreateVersion7();
        rig.ReadDb.Sources.Add(rig.Fetched(sourceId, Rig.HashA));
        rig.Corpus.Texts = [new(sourceId, rig.StoryText)];

        var result = await rig.Gate.ValidateAsync(rig.Episode, rig.Series, rig.Version(), null, Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryTooSimilarToSource);
    }

    [Fact]
    public async Task StoryApprovalGate_NullScoreWithoutCorpus_ReturnsPass()
    {
        var rig = new Rig(Topic.Ghost);
        rig.ReadDb.Sources.Add(rig.Fetched(Guid.CreateVersion7(), Rig.HashA));

        var result = await rig.Gate.ValidateAsync(rig.Episode, rig.Series, rig.Version(), null, Ct);

        result.IsError.ShouldBeFalse();
        rig.Corpus.Calls.ShouldBe(1, "the corpus is read once and an unscorable story yields no score");
    }

    [Fact]
    public async Task StoryApprovalGate_TrueCrimeAttestationNotAccepted_ReturnsAttestationRequired()
    {
        var rig = new Rig();
        var (version, _) = rig.Ready(similarityScore: 0.0m);

        var declined = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, version, new AttestationInput(false, "ta.v1"), Ct);
        var missing = await rig.Gate.ValidateAsync(rig.Episode, rig.Series, version, null, Ct);

        declined.FirstError.Code.ShouldBe(ErrorCodes.StoryAttestationRequired);
        declined.FirstError.Type.ShouldBe(ErrorType.Validation);
        missing.FirstError.Code.ShouldBe(ErrorCodes.StoryAttestationRequired);
    }

    [Fact]
    public async Task StoryApprovalGate_TrueCrimeStaleAttestationVersion_ReturnsAttestationRequired()
    {
        var rig = new Rig();
        var (version, _) = rig.Ready(similarityScore: 0.0m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, version, new AttestationInput(true, "ta.v0"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryAttestationRequired);
    }

    [Fact]
    public async Task StoryApprovalGate_TrueCrimeAllGatesPass_ReturnsAcceptedTextVersion()
    {
        var rig = new Rig();
        rig.Options.AttestationVersion = "ta.v2";
        var (version, _) = rig.Ready(similarityScore: 0.10m);

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, version, new AttestationInput(true, "ta.v2"), Ct);

        result.IsError.ShouldBeFalse();
        result.Value.AcceptedTextVersion.ShouldBe("ta.v2", "the accepted version is the configured one");
    }

    [Fact]
    public async Task StoryApprovalGate_CheckOrder_FactSheetBeforeSourcesBeforeSimilarityBeforeAttestation()
    {
        var rig = new Rig();
        var badTick = new AttestationInput(false, "ta.v0");

        // The guard answers with the sheet scripted last, so every step re-scripts its own approved sheet
        // immediately before its call; otherwise an earlier version would already be stale at 3b.
        rig.Ready();
        var stale = rig.Version(factSheetId: Guid.CreateVersion7(), similarityScore: 0.5m);
        (await rig.Gate.ValidateAsync(rig.Episode, rig.Series, stale, badTick, Ct)).FirstError.Code
            .ShouldBe(ErrorCodes.StoryFactSheetOutdated);

        var (starved, _) = rig.Ready(cited: 2, fetched: 1, similarityScore: 0.5m);
        (await rig.Gate.ValidateAsync(rig.Episode, rig.Series, starved, badTick, Ct)).FirstError.Code
            .ShouldBe(ErrorCodes.StorySourcesRequired);

        var (blocking, _) = rig.Ready(cited: 2, fetched: 2, similarityScore: 0.5m);
        (await rig.Gate.ValidateAsync(rig.Episode, rig.Series, blocking, badTick, Ct)).FirstError.Code
            .ShouldBe(ErrorCodes.StoryTooSimilarToSource);

        var (warned, _) = rig.Ready(cited: 2, fetched: 2, similarityScore: 0.10m);
        (await rig.Gate.ValidateAsync(rig.Episode, rig.Series, warned, badTick, Ct)).FirstError.Code
            .ShouldBe(ErrorCodes.StoryAttestationRequired);

        var (ready, _) = rig.Ready(cited: 2, fetched: 2, similarityScore: 0.10m);
        var accepted = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, ready, new AttestationInput(true, "ta.v1"), Ct);
        accepted.IsError.ShouldBeFalse();
        accepted.Value.AcceptedTextVersion.ShouldBe("ta.v1");
    }

    [Fact]
    public async Task StoryApprovalGate_GuardRefusesSheet_MapsFactSheetRequiredToOutdated()
    {
        // The guard's generate-time code is fact_sheet_required; approve's D8 code is story_fact_sheet_outdated.
        var rig = new Rig();

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, rig.Version(), new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.StoryFactSheetOutdated);
    }

    [Fact]
    public async Task StoryApprovalGate_GuardUnexpectedError_PassesThroughUnchanged()
    {
        var rig = new Rig();
        rig.FactSheets.Failure = Error.Failure("research_fact_sheet_broken", "The fact sheet store is down.");

        var result = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, rig.Version(), new AttestationInput(true, "ta.v1"), Ct);

        result.FirstError.Code.ShouldBe("research_fact_sheet_broken");
        result.FirstError.Type.ShouldBe(ErrorType.Failure);
    }

    [Fact]
    public async Task StoryApprovalGate_CorpusUnavailable_FailsClosedWithoutApproving()
    {
        var rig = new Rig(Topic.Ghost);
        rig.ReadDb.Sources.Add(rig.Fetched(Guid.CreateVersion7(), Rig.HashA));
        rig.Corpus.Unavailable = true;

        var result = await rig.Gate.ValidateAsync(rig.Episode, rig.Series, rig.Version(), null, Ct);

        result.IsError.ShouldBeTrue("an unreadable corpus must never let an approval through");
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        result.FirstError.Code.ShouldNotBe(ErrorCodes.StoryTooSimilarToSource);
    }

    [Fact]
    public async Task StoryApprovalGate_FictionTopicWithPinnedFactSheet_KeepsTheTrueCrimeRequirements()
    {
        // SEC-1: the story was generated from a fact sheet, so patching the topic to fiction cannot skip 3b-3e.
        var rig = new Rig(Topic.Ghost);
        var (version, _) = rig.Ready(similarityScore: 0.0m);

        var declined = await rig.Gate.ValidateAsync(rig.Episode, rig.Series, version, null, Ct);
        var accepted = await rig.Gate.ValidateAsync(
            rig.Episode, rig.Series, version, new AttestationInput(true, "ta.v1"), Ct);

        declined.FirstError.Code.ShouldBe(ErrorCodes.StoryAttestationRequired);
        accepted.IsError.ShouldBeFalse();
        accepted.Value.AcceptedTextVersion.ShouldBe(rig.Options.AttestationVersion);
    }

    /// <summary>One gate world: the domain aggregates, the fakes and the gate over the same validated options.</summary>
    private sealed class Rig
    {
        internal static readonly string HashA = new('a', 64);

        private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

        // The stored draft is long enough for the scorer's token floor (vi: 10-shingles over >= 50 tokens).
        private static readonly string LongText =
            string.Join(' ', Enumerable.Repeat("ngôi nhà bên sông vẫn im lặng mỗi đêm", 12));

        private Guid UserId { get; } = Guid.CreateVersion7();

        internal SeriesAggregate Series { get; }

        internal Episode Episode { get; }

        internal ScriptedFactSheetGuard FactSheets { get; } = new();

        internal FakeReadDb ReadDb { get; } = new();

        internal FakeCorpus Corpus { get; } = new();

        internal StoryTrueCrimeOptions Options { get; } = new();

        internal SimilaritySettings Settings { get; } = new();

        internal StoryApprovalGate Gate { get; }

        internal Rig(Topic topic = Topic.TrueCrime)
        {
            Series = SeriesAggregate.Create(UserId, new SeriesDraft("Gate rig", topic, Language.Vi, default, "voice", Guid.CreateVersion7(), null), Now);
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
        /// The true-crime side of F13: an approved sheet citing <paramref name="cited"/> sources, of which
        /// <paramref name="fetched"/> exist as fetched url rows, plus the version pinned to that sheet.
        /// </summary>
        internal (StoryVersion Version, Guid SheetId) Ready(int cited = 2, int fetched = 2, decimal? similarityScore = null)
        {
            var sheetId = Guid.CreateVersion7();
            var aliases = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var facts = new List<FactForWriter>(cited);
            for (var index = 0; index < cited; index++)
            {
                var sourceId = Guid.CreateVersion7();
                var alias = $"s{index + 1}";
                aliases[alias] = sourceId;
                facts.Add(new FactForWriter($"f{index + 1}", $"Fact {index + 1}", [alias]));
                if (index < fetched)
                {
                    ReadDb.Sources.Add(Fetched(sourceId, new string((char)('a' + index), 64)));
                }
            }

            FactSheets.ApprovedSheet = new ApprovedFactSheet(sheetId, 1, facts, aliases);
            return (Version(sheetId, similarityScore), sheetId);
        }

        internal StorySource Fetched(Guid id, string sha256)
        {
            var url = SourceUrl.Parse($"https://news.example.com/{id:N}", new FakePublicSuffixList()).Value;
            var source = StorySource.CreateUrl(id, UserId, Episode.Id, url, Now).Value;
            source.MarkFetched(url.Value, "Article", "text/html", sha256, $"research/{id:N}", 4_000, Now);
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

    /// <summary>The TestKit fake plus the two observations this suite needs: call count and a scripted failure.</summary>
    private sealed class ScriptedFactSheetGuard : IFactSheetGuard
    {
        private readonly FakeFactSheetGuard _inner = new();

        internal int Calls { get; private set; }

        internal Error? Failure { get; set; }

        internal ApprovedFactSheet? ApprovedSheet
        {
            get => _inner.ApprovedSheet;
            set => _inner.ApprovedSheet = value;
        }

        public ValueTask<ErrorOr<ApprovedFactSheet>> EnsureApprovedAsync(Guid episodeId, Topic topic, CancellationToken ct)
        {
            Calls++;
            return Failure is { } failure
                ? ValueTask.FromResult<ErrorOr<ApprovedFactSheet>>(failure)
                : _inner.EnsureApprovedAsync(episodeId, topic, ct);
        }
    }

    /// <summary>The existing hand-written EF async-query fake pattern, over the rig's own rows.</summary>
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

        internal bool Unavailable { get; set; }

        internal IReadOnlyList<CorpusText> Texts { get; set; } = [];

        public ValueTask<IReadOnlyList<CorpusText>> LoadAsync(Guid episodeId, Language lang, CancellationToken ct)
        {
            Calls++;
            return Unavailable
                ? throw new SimilarityCorpusUnavailableException(SimilarityUnavailableReason.DbError)
                : ValueTask.FromResult(Texts);
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

        // EF's AnyAsync goes through IAsyncQueryProvider; ToListAsync enumerates the async enumerable
        // instead, which is why the gate's reads needed only the enumeration path before.
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var resultType = typeof(TResult).GetGenericArguments()[0];
            var result = typeof(IQueryProvider)
                .GetMethod(nameof(IQueryProvider.Execute), 1, [typeof(Expression)])!
                .MakeGenericMethod(resultType)
                .Invoke(this, [expression]);

            return (TResult)typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [result])!;
        }
    }
}
