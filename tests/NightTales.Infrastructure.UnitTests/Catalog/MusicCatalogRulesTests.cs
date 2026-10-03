using System.Globalization;
using System.Text.Json;
using NightTales.Domain.Rendering.Music;
using NightTales.Infrastructure.Persistence.Seeding;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Catalog;

/// <summary>
/// The shared music-catalog fixtures (05 README D8, 05.3.2-T1) read through the production reader and
/// <see cref="MusicTrackRules"/>. <c>expected.json</c> also drives <c>check_catalog.py</c>, so a divergence
/// between the C# rules and the Python checker fails here, and the gain formula is pinned at its boundaries.
/// </summary>
public sealed class MusicCatalogRulesTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", "music");

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Expectations().Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_YieldsTheExpectedCode(string name)
    {
        var expected = Expectations()[name];
        var loaded = MusicCatalogFileReader.Load(File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".json")));

        if (expected is null)
        {
            loaded.IsError.ShouldBeFalse(name);
            loaded.Value.Tracks.ShouldNotBeEmpty();
        }
        else
        {
            loaded.IsError.ShouldBeTrue(name);
            loaded.Errors.Select(error => error.Code).ShouldContain(expected, name);
        }
    }

    [Fact]
    public void Fixtures_CoverEveryExpectedEntry()
    {
        Directory.GetFiles(FixtureDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name != "expected")
            .Order(StringComparer.Ordinal)
            .ShouldBe(Expectations().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Revision0_IsAllowedOnlyWhenTheCallerSaysSo()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, "invalid-revision.json"));

        MusicCatalogFileReader.Load(bytes).FirstError.Code.ShouldBe(MusicTrackRules.RevisionNotPositiveCode);
        MusicCatalogFileReader.Load(bytes, allowNonPositiveRevision: true).IsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData("-34", "0.400")]
    [InlineData("-20", "0.200")]
    [InlineData("-14", "0.100")]
    [InlineData("-3", "0.030")]
    public void ComputeBgmVolume_MatchesTheGainFormula(string lufs, string expected)
    {
        var volume = MusicTrackRules.ComputeBgmVolume(decimal.Parse(lufs, CultureInfo.InvariantCulture));

        volume.IsError.ShouldBeFalse();
        volume.Value.ShouldBe(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ComputeBgmVolume_LouderTrackGetsSmallerGain()
    {
        var quiet = MusicTrackRules.ComputeBgmVolume(-24m).Value;
        var loud = MusicTrackRules.ComputeBgmVolume(-12m).Value;

        loud.ShouldBeLessThan(quiet);
    }

    [Theory]
    [InlineData("-80", "0.400")]
    [InlineData("-71", "0.400")]
    [InlineData("1", "0.030")]
    public void ComputeBgmVolume_ClampsInsteadOfRefusing(string lufs, string expected)
    {
        // Parity with check_catalog.py _boundary_cases: the clamp is D8's only range.
        var volume = MusicTrackRules.ComputeBgmVolume(decimal.Parse(lufs, CultureInfo.InvariantCulture));

        volume.IsError.ShouldBeFalse();
        volume.Value.ShouldBe(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("-20.044", "0.201", MusicTrackRules.LoudnessScaleCode)]
    [InlineData("-20.0", "0.201", MusicTrackRules.BgmVolumeFormulaMismatchCode)]
    public void Validate_ChecksTheGainAgainstThePersistedLoudness(string lufs, string gain, string code)
    {
        var result = MusicTrackRules.Validate(Candidate(
            loudness: decimal.Parse(lufs, CultureInfo.InvariantCulture),
            gain: decimal.Parse(gain, CultureInfo.InvariantCulture)));

        result.IsError.ShouldBeTrue();
        result.Errors.Select(error => error.Code).ShouldContain(code);
    }

    [Theory]
    [InlineData(3600, true)]
    [InlineData(3600.1, false)]
    public void Validate_CapsTheDuration(double seconds, bool valid)
    {
        var result = MusicTrackRules.Validate(Candidate(duration: (decimal)seconds));

        result.IsError.ShouldBe(!valid);
        if (!valid)
        {
            result.Errors.Select(error => error.Code).ShouldContain(MusicTrackRules.DurationAboveMaximumCode);
        }
    }

    [Theory]
    [InlineData("", "Artist", "https://example.invalid/t", MusicTrackRules.TitleBlankCode)]
    [InlineData("Title", "  ", "https://example.invalid/t", MusicTrackRules.ArtistBlankCode)]
    [InlineData("Title", "Artist", "http://example.invalid/t", MusicTrackRules.SourceUrlInvalidCode)]
    [InlineData("Title", "Artist", "/relative", MusicTrackRules.SourceUrlInvalidCode)]
    public void Validate_RejectsBlankTextAndNonHttpsSources(string title, string artist, string url, string code)
    {
        var result = MusicTrackRules.Validate(Candidate(title: title, artist: artist, url: url));

        result.IsError.ShouldBeTrue();
        result.Errors.Select(error => error.Code).ShouldContain(code);
    }

    [Fact]
    public void Validate_AcceptsAWellFormedTrack() => MusicTrackRules.Validate(Candidate()).IsError.ShouldBeFalse();

    private static MusicTrackCandidate Candidate(
        string title = "Title",
        string artist = "Artist",
        string url = "https://example.invalid/t",
        decimal duration = 120m,
        decimal loudness = -20m,
        decimal gain = 0.200m) =>
        new(
            "track-a",
            "track-a.mp3",
            title,
            artist,
            url,
            MusicLicense.Cc0,
            null,
            MusicPlatformScope.Any,
            ["calm"],
            duration,
            loudness,
            gain,
            new string('a', 64),
            true);

    [Fact]
    public void TopicMoods_AreTheD8Map()
    {
        MusicTrackRules.TopicMoods(NightTales.Domain.Series.Topic.Ghost).ShouldBe(["dark_ambient", "suspense"]);
        MusicTrackRules.TopicMoods(NightTales.Domain.Series.Topic.Spiritual).ShouldBe(["temple", "meditation", "calm"]);
        MusicTrackRules.TopicMoods(NightTales.Domain.Series.Topic.TrueCrime).ShouldBe(["noir", "suspense"]);
    }

    /// <summary>The expectation table: fixture name → expected error code, or <c>null</c> when valid.</summary>
    private static Dictionary<string, string?> Expectations()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureDirectory, "expected.json")));
        var table = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            table[property.Name] = property.Value.TryGetProperty("code", out var code) ? code.GetString() : null;
        }

        return table;
    }
}
