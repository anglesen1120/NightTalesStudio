using NightTales.Domain.Rendering.Music;
using NightTales.Domain.Series;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Rendering;

/// <summary>
/// <see cref="MusicSelector.Pick"/> (05.3.2-T3, 05 README D8/M7): the mood and scope filter, the avoid-recent
/// rule and its relaxation, the ordinal order and the episode-seeded index. Every case asserts the whole
/// <see cref="MusicChoice"/> (<c>TrackId</c>, <c>File</c>, <c>BgmVolume</c>).
/// </summary>
public sealed class MusicSelectorTests
{
    private static readonly Guid Episode = Guid.Parse("01920000-0000-7000-8000-0000000000aa");

    [Fact]
    public void Pick_is_deterministic_per_episode()
    {
        var catalog = GhostCatalog();
        var input = new MusicSelectionInput(Topic.Ghost, [], Episode);

        var first = MusicSelector.Pick(input, catalog);
        var second = MusicSelector.Pick(input, catalog);
        var shuffled = MusicSelector.Pick(input, [.. catalog.AsEnumerable().Reverse()]);

        first.ShouldNotBeNull();
        second.ShouldBe(first);
        shuffled.ShouldBe(first, "the pick does not depend on the order the catalog arrives in");

        var other = MusicSelector.Pick(input with { EpisodeId = Guid.Parse("01920000-0000-7000-8000-0000000000bb") }, catalog);
        other.ShouldNotBeNull();
    }

    [Fact]
    public void Pick_avoids_recent_tracks()
    {
        var catalog = GhostCatalog();
        var all = MusicSelector.Pick(new MusicSelectionInput(Topic.Ghost, [], Episode), catalog)!;

        // The track the episode would pick without history is dropped when it is the most recent one.
        var avoided = MusicSelector.Pick(new MusicSelectionInput(Topic.Ghost, [all.TrackId], Episode), catalog)!;

        avoided.TrackId.ShouldNotBe(all.TrackId);
        var expected = catalog.Single(track => track.TrackId == avoided.TrackId);
        avoided.ShouldBe(new MusicChoice(expected.TrackId, expected.FileName, expected.BgmVolume));
    }

    [Fact]
    public void Pick_relaxes_when_all_candidates_recent()
    {
        var catalog = GhostCatalog();
        var recent = catalog.Select(track => track.TrackId).ToList();

        var expectedWithoutHistory = MusicSelector.Pick(new MusicSelectionInput(Topic.Ghost, [], Episode), catalog)!;
        var relaxed = MusicSelector.Pick(new MusicSelectionInput(Topic.Ghost, recent, Episode), catalog, avoidRecent: recent.Count);

        relaxed.ShouldBe(expectedWithoutHistory, "an emptied candidate set keeps the recent tracks");
    }

    [Fact]
    public void Pick_only_drops_the_newest_avoid_recent_ids()
    {
        var catalog = GhostCatalog();
        var ids = catalog
            .Where(track => track.TrackId.StartsWith("ghost-", StringComparison.Ordinal))
            .Select(track => track.TrackId)
            .Order(StringComparer.Ordinal)
            .ToList(); // ghost-a .. ghost-e: all five candidates

        // The recent list is newest-first (IRecentMusicQuery). Dropping the newest four of five leaves
        // exactly one candidate, whichever direction the ids arrive in.
        var choice = MusicSelector.Pick(
            new MusicSelectionInput(Topic.Ghost, [ids[0], ids[1], ids[2], ids[3]], Episode),
            catalog,
            avoidRecent: 4);

        choice.ShouldNotBeNull();
        choice.TrackId.ShouldBe(ids[4], "only the oldest of the recent picks survives");

        var reversed = MusicSelector.Pick(
            new MusicSelectionInput(Topic.Ghost, [ids[4], ids[3], ids[2], ids[1]], Episode),
            catalog,
            avoidRecent: 4);

        reversed!.TrackId.ShouldBe(ids[0]);
    }

    [Fact]
    public void Pick_respects_platform_scope()
    {
        var any = Track("any-ghost", ["dark_ambient"], MusicPlatformScope.Any, 0.150m);
        var youtube = Track("youtube-ghost", ["suspense"], MusicPlatformScope.YouTube, 0.120m);
        var inactive = Track("inactive-ghost", ["suspense"], MusicPlatformScope.Any, 0.110m, active: false);
        var wrongMood = Track("temple-only", ["temple"], MusicPlatformScope.Any, 0.100m);

        // 'youtube' and 'any' are both allowed here: 07 enforces the platform rule (CR-8), not the selector.
        var picks = Enumerable.Range(1, 40)
            .Select(i => MusicSelector.Pick(
                new MusicSelectionInput(Topic.Ghost, [], new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1])),
                [any, youtube, inactive, wrongMood])!)
            .Select(choice => choice.TrackId)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        picks.ShouldBe(["any-ghost", "youtube-ghost"]);
    }

    [Fact]
    public void Pick_orders_by_id_ordinal()
    {
        // 'B-..' sorts before 'a-..' ordinally (culture comparison would put 'a' first), and the only
        // candidate set of two lets the episode index select either; both outcomes pin the order.
        var upper = Track("b-track", ["calm"], MusicPlatformScope.Any, 0.111m);
        var lower = Track("a-track", ["calm"], MusicPlatformScope.Any, 0.222m);
        var topic = Topic.Spiritual;

        var seen = new Dictionary<uint, string>();
        for (var i = 0; i < 64; i++)
        {
            var episode = new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]);
            var choice = MusicSelector.Pick(new MusicSelectionInput(topic, [], episode), [upper, lower])!;
            var hash = System.Security.Cryptography.SHA256.HashData(episode.ToByteArray());
            var index = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 2;

            // Candidates ordered by TrackId ordinal are [a-track, b-track].
            choice.TrackId.ShouldBe(index == 0 ? "a-track" : "b-track");
            choice.File.ShouldBe(choice.TrackId + ".mp3");
            choice.BgmVolume.ShouldBe(choice.TrackId == "a-track" ? 0.222m : 0.111m);
            seen[index] = choice.TrackId;
        }

        seen.Count.ShouldBe(2, "both indexes occur over 64 episodes");
    }

    [Fact]
    public void Pick_returns_null_when_no_candidate()
    {
        var temple = Track("temple-only", ["temple"], MusicPlatformScope.Any, 0.100m);
        var inactive = Track("inactive", ["noir"], MusicPlatformScope.Any, 0.100m, active: false);

        MusicSelector.Pick(new MusicSelectionInput(Topic.TrueCrime, [], Episode), [temple, inactive]).ShouldBeNull();
        MusicSelector.Pick(new MusicSelectionInput(Topic.Ghost, [], Episode), []).ShouldBeNull();
    }

    [Fact]
    public void Pick_is_deterministic_under_duplicate_moods()
    {
        // A track matching both topic moods is still one candidate: it must not weigh double in the index.
        var both = Track("both-moods", ["dark_ambient", "suspense"], MusicPlatformScope.Any, 0.140m);
        var single = Track("single-mood", ["suspense"], MusicPlatformScope.Any, 0.130m);
        var input = new MusicSelectionInput(Topic.Ghost, [], Episode);

        var first = MusicSelector.Pick(input, [both, single]);
        var second = MusicSelector.Pick(input, [single, both]);

        first.ShouldNotBeNull();
        second.ShouldBe(first);

        var hash = System.Security.Cryptography.SHA256.HashData(Episode.ToByteArray());
        var index = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 2;
        first.TrackId.ShouldBe(index == 0 ? "both-moods" : "single-mood");
    }

    [Fact]
    public void Pick_uses_the_documented_index_formula()
    {
        var catalog = GhostCatalog();
        var ordered = catalog
            .Where(track => track.TrackId.StartsWith("ghost-", StringComparison.Ordinal))
            .OrderBy(track => track.TrackId, StringComparer.Ordinal)
            .ToList();
        var hash = System.Security.Cryptography.SHA256.HashData(Episode.ToByteArray());
        var index = (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % (uint)ordered.Count);

        var choice = MusicSelector.Pick(new MusicSelectionInput(Topic.Ghost, [], Episode), catalog);

        choice.ShouldBe(new MusicChoice(ordered[index].TrackId, ordered[index].FileName, ordered[index].BgmVolume));
    }

    private static List<MusicTrack> GhostCatalog() =>
    [
        Track("ghost-a", ["dark_ambient"], MusicPlatformScope.Any, 0.101m),
        Track("ghost-b", ["suspense"], MusicPlatformScope.YouTube, 0.102m),
        Track("ghost-c", ["dark_ambient", "suspense"], MusicPlatformScope.Any, 0.103m),
        Track("ghost-d", ["suspense"], MusicPlatformScope.Any, 0.104m),
        Track("ghost-e", ["dark_ambient"], MusicPlatformScope.YouTube, 0.105m),
        Track("temple-x", ["temple"], MusicPlatformScope.Any, 0.106m),
    ];

    private static MusicTrack Track(
        string id,
        string[] moods,
        MusicPlatformScope scope,
        decimal volume,
        bool active = true) =>
        MusicTrack.Create(
            id,
            id + ".mp3",
            id,
            "Artist",
            "https://example.invalid/" + id,
            scope == MusicPlatformScope.YouTube ? MusicLicense.YouTubeAudioLibrary : MusicLicense.Cc0,
            null,
            scope,
            moods,
            120m,
            -20m,
            volume,
            new string('a', 64),
            "music/" + id + ".mp3",
            1,
            active);
}
