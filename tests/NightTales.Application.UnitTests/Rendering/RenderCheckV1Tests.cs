using System.Text.Json;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// The <c>render-check.v1</c> DTO of 05.5.1-T2 against the D14 fixtures (05 README D9/D14): every
/// fixture deserialises with <c>UnmappedMemberHandling.Disallow</c> and re-serialises to an equal
/// document, so the C# names, the null-serialisation of the unmeasurable blocks (T65) and the strict
/// unknown-member rule are all pinned to the bytes the adapter and mock-mpt serve.
/// </summary>
public sealed class RenderCheckV1Tests
{
    public static TheoryData<string> RenderCheckFixtures => RenderFixtures.CheckTheoryData();

    [Theory]
    [MemberData(nameof(RenderCheckFixtures))]
    public void Fixture_round_trip_is_lossless(string file)
    {
        var json = RenderFixtures.ReadJson(file);

        var check = RenderFixtures.ReadCheck(file);

        var roundTripped = JsonSerializer.Serialize(check, RenderNodeJson.Strict);
        RenderFixtures.DocumentsAreEqual(json, roundTripped).ShouldBeTrue($"'{file}' must round-trip");

        // Reading the canonical document again must produce the same canonical document. Record
        // equality is not usable here: the collection members (warnings/errors) compare by reference.
        var canonical = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<RenderCheckV1>(roundTripped, RenderNodeJson.Strict),
            RenderNodeJson.Strict);
        canonical.ShouldBe(roundTripped);
    }

    [Fact]
    public void Unmeasurable_blocks_are_null_not_sentinel_numbers()
    {
        var truncated = RenderFixtures.ReadCheck("render-check.v1.truncated.json");
        var silent = RenderFixtures.ReadCheck("render-check.v1.silent.json");
        var loudness = silent.Loudness;

        truncated.Container.ShouldBeNull();
        truncated.Video.ShouldBeNull();
        truncated.Audio.ShouldBeNull();
        truncated.Narration.ShouldBeNull();
        truncated.Loudness.ShouldBeNull();
        truncated.Silence.ShouldBeNull();
        truncated.Subtitles.ShouldBeNull();
        truncated.Output.ShouldBeNull();
        loudness.ShouldNotBeNull();
        loudness!.Before.ShouldBeNull();
        loudness.After.ShouldBeNull();
        silent.Errors.ShouldBe(["audio_silent"]);
    }

    [Fact]
    public void Unknown_member_is_rejected()
    {
        var json = RenderFixtures.ReadJson("render-check.v1.ok.json");
        var patched = json.Replace("{\n  \"schema\"", "{\n  \"unexpected\": 1,\n  \"schema\"", StringComparison.Ordinal);

        patched.ShouldNotBe(json);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<RenderCheckV1>(patched, RenderNodeJson.Strict));
    }
}
