using NightTales.Application.Common.Errors;
using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>
/// The invariants of <see cref="SourceCandidate"/> (02.7.2 plan §3): the factory's guards and the only two
/// status transitions, including the error a second action gets.
/// </summary>
public sealed class SourceCandidateTests
{
    private const string Url = "https://vi.wikipedia.org/wiki/Ma";

    [Fact]
    public void Create_SetsTheCitationAndProposes()
    {
        var userId = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();

        var candidate = SourceCandidate.Create(
            userId, episodeId, runId, Url, Url, "wikipedia.org", "  Ma  ", "Wikipedia tiếng Việt", 3);

        candidate.Id.ShouldNotBe(Guid.Empty);
        candidate.UserId.ShouldBe(userId);
        candidate.EpisodeId.ShouldBe(episodeId);
        candidate.GenerationRunId.ShouldBe(runId);
        candidate.Url.ShouldBe(Url);
        candidate.NormalizedUrl.ShouldBe(Url);
        candidate.RegistrableDomain.ShouldBe("wikipedia.org");
        candidate.Title.ShouldBe("Ma", "the citation title is trimmed");
        candidate.SiteName.ShouldBe("Wikipedia tiếng Việt");
        candidate.Rank.ShouldBe((short)3);
        candidate.Status.ShouldBe(SourceCandidateStatus.Proposed);
        candidate.SourceId.ShouldBeNull();
    }

    [Fact]
    public void Create_EmptyTitle_BecomesNull()
    {
        Candidate(title: "   ").Title.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutUrl_Fails(string? url)
    {
        var failure = Should.Throw<ArgumentException>(() => Candidate(url: url!));
        failure.ParamName.ShouldBe("url");
    }

    [Fact]
    public void Create_UrlOverTheCap_Fails()
    {
        var longUrl = "https://example.com/" + new string('a', SourceCandidate.UrlMaxChars);

        Should.Throw<ArgumentException>(() => Candidate(normalizedUrl: longUrl))
            .ParamName.ShouldBe("normalizedUrl");
    }

    [Fact]
    public void Create_WithoutDomain_Fails()
    {
        Should.Throw<ArgumentException>(() => Candidate(registrableDomain: " "))
            .ParamName.ShouldBe("registrableDomain");
    }

    [Fact]
    public void Create_WithoutSiteName_Fails()
    {
        Should.Throw<ArgumentException>(() => Candidate(siteName: ""))
            .ParamName.ShouldBe("siteName");
    }

    [Fact]
    public void Create_TitleOverTheCap_Fails()
    {
        Should.Throw<ArgumentException>(() => Candidate(title: new string('t', SourceCandidate.TitleMaxChars + 1)))
            .ParamName.ShouldBe("title");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void Create_RankOutsideOneToTen_Fails(short rank)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Candidate(rank: rank)).ParamName.ShouldBe("rank");
    }

    [Fact]
    public void Create_WithoutOwnerEpisodeOrRun_Fails()
    {
        Should.Throw<ArgumentException>(() => SourceCandidate.Create(
            Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), Url, Url, "wikipedia.org", null, "W", 1))
            .ParamName.ShouldBe("userId");
        Should.Throw<ArgumentException>(() => SourceCandidate.Create(
            Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), Url, Url, "wikipedia.org", null, "W", 1))
            .ParamName.ShouldBe("episodeId");
        Should.Throw<ArgumentException>(() => SourceCandidate.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, Url, Url, "wikipedia.org", null, "W", 1))
            .ParamName.ShouldBe("generationRunId");
    }

    [Fact]
    public void MarkAdded_Proposed_RecordsTheSource()
    {
        var candidate = Candidate();
        var sourceId = Guid.CreateVersion7();

        candidate.MarkAdded(sourceId).IsError.ShouldBeFalse();

        candidate.Status.ShouldBe(SourceCandidateStatus.Added);
        candidate.SourceId.ShouldBe(sourceId);
    }

    [Fact]
    public void SourceCandidate_MarkAdded_FromDismissed_Fails()
    {
        var candidate = Candidate();
        candidate.Dismiss().IsError.ShouldBeFalse();

        var result = candidate.MarkAdded(Guid.CreateVersion7());

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SourceCandidate.NotProposedCode);
        candidate.SourceId.ShouldBeNull();
    }

    [Fact]
    public void MarkAdded_Twice_Fails()
    {
        var candidate = Candidate();
        candidate.MarkAdded(Guid.CreateVersion7()).IsError.ShouldBeFalse();

        var second = candidate.MarkAdded(Guid.CreateVersion7());

        second.IsError.ShouldBeTrue();
        second.FirstError.Code.ShouldBe(SourceCandidate.NotProposedCode);
    }

    [Fact]
    public void Dismiss_Proposed_Dismisses()
    {
        var candidate = Candidate();

        candidate.Dismiss().IsError.ShouldBeFalse();

        candidate.Status.ShouldBe(SourceCandidateStatus.Dismissed);
    }

    [Fact]
    public void Dismiss_Twice_NoOp()
    {
        var candidate = Candidate();
        candidate.Dismiss().IsError.ShouldBeFalse();

        var second = candidate.Dismiss();

        second.IsError.ShouldBeFalse("a retried or double-clicked dismiss answers 204 both times");
        candidate.Status.ShouldBe(SourceCandidateStatus.Dismissed);
    }

    [Fact]
    public void Dismiss_Added_Fails()
    {
        var candidate = Candidate();
        candidate.MarkAdded(Guid.CreateVersion7()).IsError.ShouldBeFalse();

        var result = candidate.Dismiss();

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SourceCandidate.NotProposedCode);
        candidate.Status.ShouldBe(SourceCandidateStatus.Added);
    }

    [Fact]
    public void MarkAdded_WithoutSourceId_Throws() =>
        Should.Throw<ArgumentException>(() => Candidate().MarkAdded(Guid.Empty)).ParamName.ShouldBe("sourceId");

    [Fact]
    public void SourceCandidate_NotProposedCode_MatchesTheErrorCatalogue()
    {
        // The Domain cannot reference Application/Common/Errors/ErrorCodes.cs (D1: ErrorOr is its only package),
        // so it spells the code itself; this test is the join between the two.
        SourceCandidate.NotProposedCode.ShouldBe(ErrorCodes.SourceCandidateNotProposed);
    }

    private static SourceCandidate Candidate(
        string? url = Url,
        string? normalizedUrl = Url,
        string? registrableDomain = "wikipedia.org",
        string? title = null,
        string? siteName = "Wikipedia",
        short rank = 1) =>
        SourceCandidate.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            url!,
            normalizedUrl!,
            registrableDomain!,
            title,
            siteName!,
            rank);
}
