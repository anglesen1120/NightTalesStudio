using System.Globalization;
using NightTales.Application.Features.SeriesBible;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible;

/// <summary>
/// The summary merge of <c>PUT /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U2): the client owns the
/// title and the text, the server owns <c>structure</c> and <c>approved_at</c> — kept from the stored
/// content for an episode the bible already describes, derived from the approved story version for one the
/// client newly adds, and left at the placeholder (so the validator refuses it) for an episode that is
/// unknown or not approved.
/// </summary>
public sealed class BibleSummaryDerivationTests
{
    private static readonly Guid StoredEpisode = Guid.Parse("1a0d1a2c-0a2b-4c3d-8e4f-5a6b7c8d9e0f");
    private static readonly Guid NewEpisode = Guid.Parse("2b1e2b3d-1b3c-4d4e-9f50-6b7c8d9e0f10");
    private static readonly Guid UnknownEpisode = Guid.Parse("3c2f3c4e-2c4d-4e5f-a061-7c8d9e0f1a2b");
    private static readonly DateTimeOffset StoredAt =
        DateTimeOffset.Parse("2026-01-02T03:04:05Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset ApprovedAt =
        DateTimeOffset.Parse("2026-02-03T04:05:06Z", CultureInfo.InvariantCulture);

    private static readonly StoryStructureTags StoredStructure =
        new("ghost", "forest", "arrival", "reversal", "first", ["beat one"]);

    private static readonly StoryStructureTags DerivedStructure =
        new("true_crime", "city", "cold open", "confession", "third", ["beat two", "beat three"]);

    /// <summary>What the DTO builds: a placeholder the merge must replace or leave for the validator.</summary>
    private static readonly StoryStructureTags Placeholder = StoryStructureTags.Empty;

    private static BibleEpisodeSummary Summary(
        Guid episodeId,
        string title,
        string text,
        StoryStructureTags structure,
        DateTimeOffset approvedAt) => new(episodeId, title, text, structure, approvedAt);

    private static Dictionary<Guid, BibleSummaryServerValues> NoDerived() => [];

    [Fact]
    public void Merge_KnownEpisode_TakesTheClientsTitleAndTextAndKeepsTheStoredServerValues()
    {
        var stored = new[]
        {
            Summary(StoredEpisode, "Old title", "Old text", StoredStructure, StoredAt),
        };
        var requested = new[]
        {
            Summary(StoredEpisode, "New title", "New text", Placeholder, StoredAt.AddDays(9)),
        };

        var merged = BibleSummaryDerivation.Merge(requested, stored, NoDerived());

        var summary = merged.ShouldHaveSingleItem();
        summary.EpisodeId.ShouldBe(StoredEpisode);
        summary.Title.ShouldBe("New title");
        summary.Summary.ShouldBe("New text");
        summary.Structure.ShouldBe(StoredStructure);
        summary.ApprovedAt.ShouldBe(StoredAt);
    }

    [Fact]
    public void Merge_NewEpisode_TakesTheDerivedServerValues()
    {
        var derived = new Dictionary<Guid, BibleSummaryServerValues>
        {
            [NewEpisode] = new(DerivedStructure, ApprovedAt),
        };

        var merged = BibleSummaryDerivation.Merge(
            [Summary(NewEpisode, "Title", "Text", Placeholder, default)],
            [],
            derived);

        var summary = merged.ShouldHaveSingleItem();
        summary.Title.ShouldBe("Title");
        summary.Summary.ShouldBe("Text");
        summary.Structure.ShouldBe(DerivedStructure);
        summary.ApprovedAt.ShouldBe(ApprovedAt);
    }

    [Fact]
    public void Merge_UnknownOrUnapprovedEpisode_KeepsThePlaceholder()
    {
        var merged = BibleSummaryDerivation.Merge(
            [Summary(UnknownEpisode, "Title", "Text", Placeholder, default)],
            [],
            NoDerived());

        var summary = merged.ShouldHaveSingleItem();
        summary.Structure.ShouldBe(Placeholder);
        summary.ApprovedAt.ShouldBe(default);
    }

    [Fact]
    public void Merge_HoldsExactlyTheRequestedSummariesInBodyOrder()
    {
        var stored = new[]
        {
            Summary(StoredEpisode, "Old title", "Old text", StoredStructure, StoredAt),
        };
        var derived = new Dictionary<Guid, BibleSummaryServerValues>
        {
            [NewEpisode] = new(DerivedStructure, ApprovedAt),
        };

        var merged = BibleSummaryDerivation.Merge(
            [
                Summary(NewEpisode, "Added", "Text", Placeholder, default),
                Summary(StoredEpisode, "Edited", "Text", Placeholder, default),
            ],
            stored,
            derived);

        merged.Count.ShouldBe(2);
        merged[0].EpisodeId.ShouldBe(NewEpisode);
        merged[1].EpisodeId.ShouldBe(StoredEpisode);
    }

    [Fact]
    public void Merge_RemovedSummary_StaysRemoved()
    {
        var stored = new[]
        {
            Summary(StoredEpisode, "Old title", "Old text", StoredStructure, StoredAt),
        };

        BibleSummaryDerivation.Merge([], stored, NoDerived()).ShouldBeEmpty();
    }

    [Fact]
    public void Merge_NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() =>
            BibleSummaryDerivation.Merge(null!, [], NoDerived()));
        Should.Throw<ArgumentNullException>(() =>
            BibleSummaryDerivation.Merge([], null!, NoDerived()));
        Should.Throw<ArgumentNullException>(() =>
            BibleSummaryDerivation.Merge([], [], null!));
    }
}
