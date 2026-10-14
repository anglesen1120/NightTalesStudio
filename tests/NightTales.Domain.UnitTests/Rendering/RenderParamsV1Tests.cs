using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Images;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Rendering;

/// <summary>
/// <c>render-params.v1</c> (05 README D11): the stored document's shape. `schema` is written first and the
/// members follow the D11 order, a member the record does not have is rejected on read (the D14 rule applied
/// to the stored document too), and every block keeps its D11 key names. <see cref="ImagePlanSummary.From"/>
/// is the counts-only projection of 03 D4 — keyword and prompt text must not reach it.
/// </summary>
public sealed class RenderParamsV1Tests
{
    private const string StylePromptVersion = "style=dark-folklore-ink@1a2b3c4d;neg=v1";
    private const string Keyword = "moonlight over the temple";

    /// <summary>The mapper's writer (05.1.2-T1 Design); the golden fixture comparison uses it indented.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The D14 reader rule: an unknown member is an error, not something to ignore.</summary>
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Fact]
    public void Schema_is_written_first_then_the_d11_member_order()
    {
        Keys(JsonSerializer.Serialize(Sample(), Web)).ShouldBe(
        [
            "schema",
            "mapper_version",
            "story_version_id",
            "aspect",
            "language",
            "voice_id",
            "style_template_id",
            "style_prompt_version",
            "image_plan",
            "subtitles",
            "music",
            "clip_seconds",
        ]);
    }

    [Fact]
    public void Blocks_keep_the_d11_keys_and_their_order()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Sample(), Web))!.AsObject();

        Keys(node["image_plan"]!.ToJsonString()).ShouldBe(
        [
            "clip_seconds",
            "needed",
            "max_terms",
            "planned",
            "trimmed",
            "hints",
            "slot_segments",
            "style_prompt_version",
        ]);
        Keys(node["subtitles"]!.ToJsonString()).ShouldBe(["enabled", "preset"]);
        Keys(node["music"]!.ToJsonString()).ShouldBe(["track_id", "bgm_volume", "attribution"]);
    }

    [Fact]
    public void Round_trips_with_unmapped_members_disallowed()
    {
        var original = Sample();

        var read = JsonSerializer.Deserialize<RenderParamsV1>(JsonSerializer.Serialize(original, Web), Strict)!;

        read.Schema.ShouldBe(RenderParamsV1.SchemaName);
        read.MapperVersion.ShouldBe(RenderParamsV1.CurrentMapperVersion);
        read.StoryVersionId.ShouldBe(original.StoryVersionId);
        read.Aspect.ShouldBe("landscape");
        read.Language.ShouldBe("vi");
        read.VoiceId.ShouldBe("Hải Đăng");
        read.StyleTemplateId.ShouldBe(original.StyleTemplateId);
        read.StylePromptVersion.ShouldBe(StylePromptVersion);
        read.ClipSeconds.ShouldBe(10);
        read.Subtitles.Enabled.ShouldBeTrue();
        read.Subtitles.Preset.ShouldBe("landscape@1");
        var music = read.Music.ShouldNotBeNull();
        music.TrackId.ShouldBe("dark-ambient-01");
        music.BgmVolume.ShouldBe(0.146m);
        music.Attribution.ShouldBe("YouTube Audio Library");
        read.ImagePlan.ClipSeconds.ShouldBe(10);
        read.ImagePlan.Needed.ShouldBe(3);
        read.ImagePlan.MaxTerms.ShouldBe(4);
        read.ImagePlan.Planned.ShouldBe(2);
        read.ImagePlan.Trimmed.ShouldBe(1);
        read.ImagePlan.Hints.ShouldBe(original.ImagePlan.Hints, ignoreOrder: false);
        read.ImagePlan.SlotSegments.ShouldBe(original.ImagePlan.SlotSegments, ignoreOrder: false);
        read.ImagePlan.StylePromptVersion.ShouldBe(StylePromptVersion);
    }

    [Fact]
    public void Unknown_member_is_rejected()
    {
        // The stored document is read back by 09.6.x; a member the record lost must fail here, not be ignored.
        var node = JsonNode.Parse(JsonSerializer.Serialize(Sample(), Web))!.AsObject();
        node["n_threads"] = 4;

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<RenderParamsV1>(node.ToJsonString(), Strict));
    }

    [Fact]
    public void Missing_schema_is_rejected()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Sample(), Web))!.AsObject();
        node.Remove("schema").ShouldBeTrue();

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<RenderParamsV1>(node.ToJsonString(), Strict));
    }

    [Fact]
    public void Music_null_is_written_as_null_and_reads_back_null()
    {
        var json = JsonSerializer.Serialize(Sample() with { Music = null }, Web);

        json.ShouldContain("\"music\":null");

        var read = JsonSerializer.Deserialize<RenderParamsV1>(json, Strict)!;
        read.Music.ShouldBeNull();
    }

    [Fact]
    public void Image_plan_summary_projects_counts_and_addresses_only()
    {
        var summary = ImagePlanSummary.From(Plan(), StylePromptVersion);

        summary.ClipSeconds.ShouldBe(10);
        summary.Needed.ShouldBe(3);
        summary.MaxTerms.ShouldBe(4);
        summary.Planned.ShouldBe(2);
        summary.Trimmed.ShouldBe(1);
        summary.Hints.ShouldBe(["scenes_sparse"]);
        summary.SlotSegments.ShouldBe(["seg-1", "seg-2"]);
        summary.StylePromptVersion.ShouldBe(StylePromptVersion);

        // P8: no keyword and no prompt text, only counts and segment addresses.
        JsonSerializer.Serialize(summary, Web).ShouldNotContain(Keyword, Case.Sensitive);
    }

    [Fact]
    public void Image_plan_summary_rejects_a_blank_style_prompt_version()
    {
        Should.Throw<ArgumentException>(() => ImagePlanSummary.From(Plan(), " "));
    }

    /// <summary>A complete D11 document: landscape, vi, music picked, two of four terms planned.</summary>
    private static RenderParamsV1 Sample() => new(
        RenderParamsV1.CurrentMapperVersion,
        Guid.Parse("0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8e9f"),
        "landscape",
        "vi",
        "Hải Đăng",
        Guid.Parse("0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8ea0"),
        StylePromptVersion,
        new ImagePlanSummary(10, 3, 4, 2, 1, ["scenes_sparse"], ["seg-1", "seg-2"], StylePromptVersion),
        new SubtitlesParams(true, "landscape@1"),
        new MusicParams("dark-ambient-01", 0.146m, "YouTube Audio Library"),
        10);

    private static RenderImagePlan Plan() => new(
        [new PlannedTerm(0, "seg-1", Keyword), new PlannedTerm(1, "seg-2", "rain on the roof")],
        clipSeconds: 10,
        neededImages: 3,
        maxTerms: 4,
        trimmedCount: 1,
        hintCodes: ["scenes_sparse"]);

    private static IReadOnlyList<string> Keys(string json) =>
        [.. JsonNode.Parse(json)!.AsObject().Select(member => member.Key)];
}
