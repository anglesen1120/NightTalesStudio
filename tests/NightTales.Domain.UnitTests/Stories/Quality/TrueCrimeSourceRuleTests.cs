using NightTales.Domain.Research;
using NightTales.Domain.Stories.Quality;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Quality;

/// <summary>
/// F13 (02 README): the true-crime rule needs at least two distinct fetched url sources, each cited by a
/// kept fact of the approved sheet.
/// </summary>
public sealed class TrueCrimeSourceRuleTests
{
    private const int MinCited = 2;

    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private static readonly string HashC = new('c', 64);

    [Fact]
    public void TrueCrimeSourceRule_TwoCitedFetchedSources_Qualifies()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var count = Count([Url(first, HashA), Url(second, HashB)], [first, second]);

        count.ShouldBeGreaterThanOrEqualTo(MinCited);
        count.ShouldBe(2);
    }

    [Fact]
    public void TrueCrimeSourceRule_SecondSourceNotCited_Fails()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var count = Count([Url(first, HashA), Url(second, HashB)], [first]);

        count.ShouldBeLessThan(MinCited);
        count.ShouldBe(1);
    }

    [Fact]
    public void TrueCrimeSourceRule_SameSha256Twice_Fails()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        // Two rows, two mirrors of one article: the same fetched text can only qualify once.
        var count = Count([Url(first, HashA), Url(second, HashA)], [first, second]);

        count.ShouldBe(1);
    }

    [Fact]
    public void TrueCrimeSourceRule_UnfetchedSource_Fails()
    {
        var fetched = Guid.CreateVersion7();
        var pending = Guid.CreateVersion7();

        var count = Count(
            [Url(fetched, HashA), Url(pending, HashB, StorySourceStatus.Pending)],
            [fetched, pending]);

        count.ShouldBe(1);
    }

    [Fact]
    public void TrueCrimeSourceRule_SourceCitedOnlyByDroppedFact_Fails()
    {
        var kept = Guid.CreateVersion7();
        var dropped = Guid.CreateVersion7();

        // A dropped fact never reaches the approved sheet, so its source is not among the cited ids.
        var count = Count([Url(kept, HashA), Url(dropped, HashB)], [kept]);

        count.ShouldBe(1);
    }

    [Fact]
    public void TrueCrimeSourceRule_ThreeSourcesTwoCited_Qualifies()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();

        var count = Count([Url(first, HashA), Url(second, HashB), Url(third, HashC)], [first, second]);

        count.ShouldBeGreaterThanOrEqualTo(MinCited);
        count.ShouldBe(2);
    }

    [Fact]
    public void TrueCrimeSourceRule_UserNoteCitedButHashed_NeverQualifies()
    {
        var note = Guid.CreateVersion7();
        var article = Guid.CreateVersion7();

        var count = Count(
            [
                new TrueCrimeSourceRule.Candidate(note, StorySourceKind.UserNote, StorySourceStatus.Fetched, HashA),
                Url(article, HashB),
            ],
            [note, article]);

        count.ShouldBe(1);
    }

    [Fact]
    public void TrueCrimeSourceRule_CitedSourceWithoutHash_NeverQualifies()
    {
        var hashless = Guid.CreateVersion7();
        var hashed = Guid.CreateVersion7();

        var count = Count([Url(hashless, null), Url(hashed, HashA)], [hashless, hashed]);

        count.ShouldBe(1);
    }

    private static TrueCrimeSourceRule.Candidate Url(
        Guid id,
        string? sha256,
        StorySourceStatus status = StorySourceStatus.Fetched) =>
        new(id, StorySourceKind.Url, status, sha256);

    private static int Count(TrueCrimeSourceRule.Candidate[] sources, Guid[] cited) =>
        TrueCrimeSourceRule.QualifyingDistinctHashCount(sources, cited);
}
