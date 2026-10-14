using NightTales.Domain.Research;
using NightTales.Domain.Stories.Quality;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Quality;

/// <summary>
/// Independent QA boundary set for the pure F13 rule (02.3.3-T1, 02 README F13): what the gate's floor
/// comparison reads, so a floor of 1..10 is exact. Nothing here re-states the shipped cases; it pins the
/// edges the shipped suite leaves open — duplicate ids, every non-<c>fetched</c> status, a present-but-empty
/// hash, an unmatched cited id, and the argument contract.
/// </summary>
public sealed class TrueCrimeSourceRuleQaTests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private static readonly string HashC = new('c', 64);

    [Fact]
    public void QaTests_NoSources_NoCitedIds_CountsZero()
    {
        TrueCrimeSourceRule.QualifyingDistinctHashCount([], []).ShouldBe(0);
    }

    [Fact]
    public void QaTests_ThreeRowsOneHash_CountsOne()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();

        // Three cited ids, one content hash: a mirrored article still counts once (F13).
        var count = TrueCrimeSourceRule.QualifyingDistinctHashCount(
            [Url(first, HashA), Url(second, HashA), Url(third, HashA)],
            [first, second, third]);

        count.ShouldBe(1);
    }

    [Fact]
    public void QaTests_SameSourceCitedTwice_CountsOnce()
    {
        var only = Guid.CreateVersion7();

        // The cited set is a set: repeating one id in the approved sheet's aliases adds nothing.
        TrueCrimeSourceRule.QualifyingDistinctHashCount([Url(only, HashA)], [only, only]).ShouldBe(1);
    }

    [Fact]
    public void QaTests_CitedIdWithNoMatchingRow_CountsZero()
    {
        var present = Guid.CreateVersion7();
        var absent = Guid.CreateVersion7();

        // A fact may cite an alias that resolves to a source row that no longer exists; only rows count.
        TrueCrimeSourceRule.QualifyingDistinctHashCount([Url(present, HashA)], [absent]).ShouldBe(0);
    }

    [Theory]
    [InlineData(StorySourceStatus.Pending)]
    [InlineData(StorySourceStatus.Failed)]
    [InlineData(StorySourceStatus.Rejected)]
    public void QaTests_NonFetchedStatus_NeverQualifies(StorySourceStatus status)
    {
        var cited = Guid.CreateVersion7();

        var count = TrueCrimeSourceRule.QualifyingDistinctHashCount(
            [Url(cited, HashA, status)],
            [cited]);

        count.ShouldBe(0);
    }

    [Fact]
    public void QaTests_EmptyHashString_NeverQualifies()
    {
        var cited = Guid.CreateVersion7();

        // An empty hash is present to the compiler but not to the rule: it can never be a distinct text.
        TrueCrimeSourceRule.QualifyingDistinctHashCount([Url(cited, string.Empty)], [cited]).ShouldBe(0);
    }

    [Fact]
    public void QaTests_ThreeDistinctHashes_FiveSources_CountsThree()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();
        var hashes = new[] { HashA, HashB, HashC, HashA, HashB };

        var sources = ids.Select((id, index) => Url(id, hashes[index])).ToArray();

        TrueCrimeSourceRule.QualifyingDistinctHashCount(sources, ids).ShouldBe(3);
    }

    [Fact]
    public void QaTests_NullSources_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => TrueCrimeSourceRule.QualifyingDistinctHashCount(null!, [Guid.CreateVersion7()]));
    }

    [Fact]
    public void QaTests_NullCitedIds_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => TrueCrimeSourceRule.QualifyingDistinctHashCount([], null!));
    }

    private static TrueCrimeSourceRule.Candidate Url(
        Guid id,
        string? sha256,
        StorySourceStatus status = StorySourceStatus.Fetched) =>
        new(id, StorySourceKind.Url, status, sha256);
}
