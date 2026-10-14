using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ErrorOr;
using NightTales.Application.Abstractions.Stories;
using NightTales.Application.Features.Rendering;
using NightTales.Domain.Catalog;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Rendering.Music;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// <c>RenderSubmitMapper</c> (05.1.2-T1, 05 README D3/D11): the golden byte comparison against the three D14
/// <c>render-submit.v1.*</c> fixtures, the D3 mapping table, the four validation codes and the
/// <c>render-params.v1</c> projection 09.6.1-T1 stores.
/// </summary>
/// <remarks>
/// The fixtures are the contract artefacts (D14): they are read through <see cref="RenderNodeJson.Strict"/>
/// (unknown members are rejected) and written back with the canonical options. Each golden case passes a
/// fixture's own <c>mpt.video_terms</c> and <c>image_plan.prompt_sha256</c> back in as the prepared prompts
/// (<see cref="RenderSubmitMapper.BuildPrompts"/> produces them in production), so the whole built body is
/// compared with the fixture byte for byte.
/// </remarks>
public sealed class RenderSubmitMapperTests
{
    private const string KokoroPrefix = "kokoro:";

    /// <summary>The seed's advisory line limits (05 D6); they are not MPT fields.</summary>
    private const int LandscapeMaxCharsPerLine = 42;

    private const int PortraitMaxCharsPerLine = 28;

    private const int PresetMaxLines = 2;

    private const int MptFieldCount = 38;

    private static readonly Guid RenderJobId = Guid.Parse("0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8e9f");

    private static readonly Guid StoryVersionId = Guid.Parse("0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8ea1");

    private static readonly Guid StyleTemplateId = Guid.Parse("0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8ea2");

    private const string StylePromptVersion = "style=golden-test-style@1a2b3c4d;neg=v1";

    private static readonly string[] D11Keys =
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
    ];

    // ------------------------------------------------------------------ Golden ---------------------------------

    [Fact]
    public void Landscape_vi_matches_golden_fixture() => AssertMatchesGolden("render-submit.v1.landscape-vi.json");

    [Fact]
    public void Portrait_en_matches_golden_fixture() => AssertMatchesGolden("render-submit.v1.portrait-en.json");

    [Fact]
    public void No_music_matches_golden_fixture() => AssertMatchesGolden("render-submit.v1.no-music.json");

    // ------------------------------------------------------------------ Mapping (05 D3) -----------------------

    [Fact]
    public void Portrait_uses_custom_position()
    {
        var built = Submit(
            Params(aspect: "portrait", language: "en", preset: "portrait@1"),
            Story(language: "en"),
            DefaultPlan(),
            PortraitPreset(),
            track: null);

        built.Mpt.VideoAspect.ShouldBe("9:16");
        built.Mpt.SubtitlePosition.ShouldBe("custom");
        built.Mpt.CustomPosition.ShouldBe(62.0m);
        built.Mpt.FontSize.ShouldBe(64);
    }

    [Fact]
    public void Landscape_uses_bottom_position()
    {
        var built = Submit(Params(), Story(), DefaultPlan(), LandscapePreset(), track: null);

        built.Mpt.VideoAspect.ShouldBe("16:9");
        built.Mpt.SubtitlePosition.ShouldBe("bottom");
        built.Mpt.CustomPosition.ShouldBe(70.0m);
        built.Mpt.FontSize.ShouldBe(84);
    }

    [Fact]
    public void Script_is_normalized_to_nfc()
    {
        var built = Submit(
            Params(),
            Story(body: "Vie\u0323\u0302t Nam, e\u0301tude"),
            DefaultPlan(),
            LandscapePreset(),
            track: null);

        built.Mpt.VideoScript.ShouldBe("Việt Nam, étude");
        built.Mpt.VideoScript.ShouldNotContain("e\u0301", Case.Sensitive);
    }

    [Fact]
    public void Script_over_20k_runes_returns_script_too_long()
    {
        var body = new string('a', RenderSubmitMapper.MaxScriptRunes + 1);

        var built = Build(
            Params(), Story(body: body), DefaultPlan(), LandscapePreset(), track: null);

        built.IsError.ShouldBeTrue();
        built.FirstError.Code.ShouldBe("render.script_too_long");
    }

    [Fact]
    public void Script_at_20k_runes_succeeds()
    {
        var body = new string('a', RenderSubmitMapper.MaxScriptRunes);

        var built = Submit(Params(), Story(body: body), DefaultPlan(), LandscapePreset(), track: null);

        built.Mpt.VideoScript.Length.ShouldBe(20_000);
    }

    [Fact]
    public void Script_limit_counts_runes_not_utf16_units()
    {
        // A surrogate pair is one Rune and two UTF-16 units: 20,000 runes are 40,000 chars.
        var atLimit = string.Concat(Enumerable.Repeat("\U0001F600", RenderSubmitMapper.MaxScriptRunes));
        atLimit.Length.ShouldBe(2 * RenderSubmitMapper.MaxScriptRunes);

        Submit(Params(), Story(body: atLimit), DefaultPlan(), LandscapePreset(), track: null)
            .Mpt.VideoScript.ShouldBe(atLimit);

        var overLimit = atLimit + "\U0001F600";
        var built = Build(
            Params(), Story(body: overLimit), DefaultPlan(), LandscapePreset(), track: null);

        built.IsError.ShouldBeTrue();
        built.FirstError.Code.ShouldBe("render.script_too_long");
    }

    [Fact]
    public void Script_decomposed_text_is_measured_after_normalisation()
    {
        // 20,000 "e" + U+0301 pairs: 40,000 runes before NFC, 20,000 after — the limit is the post-NFC count.
        var decomposed = string.Concat(Enumerable.Repeat("e\u0301", RenderSubmitMapper.MaxScriptRunes));

        var built = Submit(Params(), Story(body: decomposed), DefaultPlan(), LandscapePreset(), track: null);

        built.Mpt.VideoScript.Length.ShouldBe(RenderSubmitMapper.MaxScriptRunes);
        built.Mpt.VideoScript.ShouldNotContain("\u0301", Case.Sensitive);

        var overLimit = decomposed + "e\u0301";
        var over = Build(
            Params(), Story(body: overLimit), DefaultPlan(), LandscapePreset(), track: null);

        over.IsError.ShouldBeTrue();
        over.FirstError.Code.ShouldBe("render.script_too_long");
    }

    [Fact]
    public void Video_terms_follow_prompt_order()
    {
        var style = Style();
        var plan = Plan(["alpha", "beta", "gamma"]);
        var prompts = Prompts(plan, style);

        var built = Submit(Params(plan: plan), Story(), plan, LandscapePreset(), track: null, prompts: prompts);

        string[] expected = [.. prompts.Select(prompt => prompt.Text)];
        built.Mpt.VideoTerms.ShouldBe(expected);
        built.Mpt.VideoTerms[0].ShouldStartWith("<styled scene prompt vi alpha>", Case.Sensitive);
        built.Mpt.VideoTerms[1].ShouldStartWith("<styled scene prompt vi beta>", Case.Sensitive);
        built.Mpt.VideoTerms[2].ShouldStartWith("<styled scene prompt vi gamma>", Case.Sensitive);
        built.Mpt.VideoTerms[0].ShouldBe(ScenePromptBuilder.Build(style, "alpha").Text);
    }

    [Fact]
    public void Voice_name_is_kokoro_prefixed()
    {
        var built = Submit(Params(voiceId: "Hải Đăng"), Story(), DefaultPlan(), LandscapePreset(), track: null);

        built.Mpt.VoiceName.ShouldBe("kokoro:Hải Đăng");
        built.Mpt.VoiceName.ShouldBe(KokoroPrefix + "Hải Đăng");
    }

    [Fact]
    public void Fixed_values_match_d3()
    {
        // p.ClipSeconds is deliberately not 10: video_clip_duration is the fixed D3 literal (decision of
        // 05.1.2-T1), never the stored value.
        var p = Params(music: Choice(file: "choice-file.mp3"), clipSeconds: 4);
        p.ClipSeconds.ShouldBe(4);

        var built = Submit(p, Story(), DefaultPlan(), LandscapePreset(), track: Track());

        built.Schema.ShouldBe("render-submit.v1");
        built.RenderJobId.ShouldBe(RenderJobId);
        built.Mpt.VideoFitMode.ShouldBe("cover");
        built.Mpt.VideoConcatMode.ShouldBe("sequential");
        built.Mpt.VideoTransitionMode.ShouldBeNull();
        built.Mpt.VideoClipDuration.ShouldBe(10);
        built.Mpt.VideoClipSpeed.ShouldBe(1.0m);
        built.Mpt.MatchMaterialsToScript.ShouldBeTrue();
        built.Mpt.VideoCount.ShouldBe(1);
        built.Mpt.VideoSource.ShouldBe("openai_image");
        built.Mpt.VideoMaterials.ShouldBeNull();
        built.Mpt.CustomAudioFile.ShouldBeNull();
        built.Mpt.VideoLanguage.ShouldBe("vi");
        built.Mpt.VoiceVolume.ShouldBe(1.0m);
        built.Mpt.VoiceRate.ShouldBe(1.0m);
        built.Mpt.BgmType.ShouldBe("custom");
        built.Mpt.BgmFile.ShouldBe("dark-ambient-01.mp3", "bgm_file is the catalog track's file name");
        built.Mpt.BgmVolume.ShouldBe(0.146m);
        built.Mpt.VideoMusicPrompt.ShouldBe(string.Empty);
        built.Mpt.SoniloBgmPrompt.ShouldBe(string.Empty);
        built.Mpt.SubtitleEnabled.ShouldBeTrue();
        built.Mpt.SubtitleDisplayMode.ShouldBe("sentence");
        built.Mpt.SubtitleAnimation.ShouldBe("none");
        built.Mpt.ParagraphNumber.ShouldBe(1);
        built.Mpt.VideoScriptPrompt.ShouldBe(string.Empty);
        built.Mpt.CustomSystemPrompt.ShouldBe(string.Empty);
    }

    [Fact]
    public void Camel_case_preset_maps_to_snake_case_fields()
    {
        var preset = LandscapePreset();

        var built = Submit(Params(), Story(), DefaultPlan(), preset, track: null);

        built.Mpt.FontName.ShouldBe(preset.FontName);
        built.Mpt.FontName.ShouldBe("BeVietnamPro-Bold.ttf");
        built.Mpt.FontSize.ShouldBe(preset.FontSize);
        built.Mpt.StrokeWidth.ShouldBe(preset.StrokeWidth);
        built.Mpt.StrokeColor.ShouldBe(preset.StrokeColor);
        built.Mpt.TextForeColor.ShouldBe(preset.TextForeColor);
        built.Mpt.TextBackgroundColor.ShouldBe(preset.TextBackgroundColor);
        built.Mpt.RoundedSubtitleBackground.ShouldBe(preset.RoundedSubtitleBackground);
        built.Mpt.SubtitlePosition.ShouldBe(preset.SubtitlePosition);
        built.Mpt.CustomPosition.ShouldBe(preset.CustomPosition);
        built.Mpt.SubtitleEnabled.ShouldBeTrue();

        // maxCharsPerLine / maxLines drive subtitle_fit.py only and never enter a wire document (05 D6).
        var wireNames = WireNames<MptSubmitFields>();
        wireNames.ShouldNotContain("max_chars_per_line");
        wireNames.ShouldNotContain("max_lines");
        var json = Canonical(built);
        json.ShouldNotContain("maxCharsPerLine", Case.Sensitive);
        json.ShouldNotContain("maxLines", Case.Sensitive);
    }

    [Fact]
    public void Mpt_fields_exclude_n_threads()
    {
        // F22: the adapter injects ADAPTER_MPT_THREADS, so the field does not exist here and is never sent.
        var wireNames = WireNames<MptSubmitFields>();

        wireNames.Count.ShouldBe(MptFieldCount, "the mpt object is the D3 key set exactly, minus n_threads");
        wireNames.ShouldContain("video_subject");
        wireNames.Any(name => name.Contains("thread", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse("F22: the adapter injects ADAPTER_MPT_THREADS, so no wire member is n_threads");

        var built = Submit(Params(), Story(), DefaultPlan(), LandscapePreset(), track: null);
        Canonical(built).ShouldNotContain("n_threads", Case.Sensitive);
    }

    [Fact]
    public void Image_plan_is_copied_from_the_planner()
    {
        var style = Style();
        var plan = new RenderImagePlan(
            [
                new PlannedTerm(0, "hook", "one"),
                new PlannedTerm(1, "p:1", "two"),
                new PlannedTerm(2, "ending", "three"),
            ],
            clipSeconds: 10,
            neededImages: 3,
            maxTerms: 5,
            trimmedCount: 4,
            hintCodes: ["scenes_trimmed"]);
        var prompts = Prompts(plan, style);

        var built = Submit(Params(plan: plan), Story(), plan, LandscapePreset(), track: null, prompts: prompts);

        built.ImagePlan.MaxImages.ShouldBe(5, "max_images is the plan's own cap, not the number of prompts");
        built.ImagePlan.PromptSha256.Count.ShouldBe(3);
        string[] expected = [.. prompts.Select(prompt => prompt.Sha256)];
        built.ImagePlan.PromptSha256.ShouldBe(expected);
        built.ImagePlan.PromptSha256[0].ShouldBe(ScenePromptBuilder.Sha256Hex(built.Mpt.VideoTerms[0]));
    }

    [Fact]
    public void BuildPrompts_follows_the_plan_order()
    {
        var plan = Plan(["alpha", "beta", "gamma"]);

        var prompts = Prompts(plan);

        prompts.Count.ShouldBe(plan.Terms.Count);
        prompts[0].Text.ShouldStartWith("<styled scene prompt vi alpha>", Case.Sensitive);
        prompts[1].Text.ShouldStartWith("<styled scene prompt vi beta>", Case.Sensitive);
        prompts[2].Text.ShouldStartWith("<styled scene prompt vi gamma>", Case.Sensitive);
    }

    [Fact]
    public void BuildPrompts_matches_the_prompt_builder()
    {
        var style = Style();
        var plan = Plan(["one", "two"]);

        var prompts = Prompts(plan, style);

        ScenePrompt[] expected = [ScenePromptBuilder.Build(style, "one"), ScenePromptBuilder.Build(style, "two")];
        prompts.ShouldBe(expected);
    }

    // ------------------------------------------------------------------ Failures and edge cases ---------------

    [Fact]
    public void Aspect_invalid_returns_aspect_invalid()
    {
        var built = Build(
            Params(aspect: "square"), Story(), DefaultPlan(), LandscapePreset(), track: null);

        built.IsError.ShouldBeTrue();
        built.FirstError.Code.ShouldBe("render.aspect_invalid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Subject_outside_1_to_200_returns_subject_invalid(int length)
    {
        var built = Build(
            Params(), Story(title: new string('t', length)), DefaultPlan(), LandscapePreset(), track: null);

        built.IsError.ShouldBeTrue();
        built.FirstError.Code.ShouldBe("render.subject_invalid");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void Subject_at_1_and_200_chars_succeeds(int length)
    {
        var title = new string('t', length);

        var built = Submit(Params(), Story(title: title), DefaultPlan(), LandscapePreset(), track: null);

        built.Mpt.VideoSubject.ShouldBe(title);
    }

    [Fact]
    public void Terms_empty_or_over_200_returns_terms_invalid()
    {
        var empty = Plan([]);
        var emptyBuilt = Build(
            Params(plan: empty), Story(), empty, LandscapePreset(), track: null);

        emptyBuilt.IsError.ShouldBeTrue();
        emptyBuilt.FirstError.Code.ShouldBe("render.terms_invalid");

        var tooMany = Plan([.. Enumerable.Range(0, 201).Select(index => index.ToString(CultureInfo.InvariantCulture))]);
        var tooManyBuilt = Build(
            Params(plan: tooMany), Story(), tooMany, LandscapePreset(), track: null);

        tooManyBuilt.IsError.ShouldBeTrue();
        tooManyBuilt.FirstError.Code.ShouldBe("render.terms_invalid");
    }

    [Fact]
    public void Term_length_boundary_is_1000_chars_on_the_prompt_text()
    {
        var style = Style();
        var emptyKeywordLength = ScenePromptBuilder.Build(style, string.Empty).Text.Length;

        // The boundary is the D3 limit on the string that is sent, i.e. the built prompt text, not the keyword.
        var atLimit = Plan([new string('k', RenderSubmitMapper.MaxTermLength - emptyKeywordLength)]);
        Prompts(atLimit, style)[0].Text.Length.ShouldBe(1_000);

        var built = Submit(
            Params(plan: atLimit), Story(), atLimit, LandscapePreset(), track: null, prompts: Prompts(atLimit, style));

        built.Mpt.VideoTerms[0].Length.ShouldBe(1_000);

        var overLimit = Plan([new string('k', RenderSubmitMapper.MaxTermLength - emptyKeywordLength + 1)]);
        var over = Build(
            Params(plan: overLimit),
            Story(),
            overLimit,
            LandscapePreset(),
            track: null,
            prompts: Prompts(overLimit, style));

        over.IsError.ShouldBeTrue();
        over.FirstError.Code.ShouldBe("render.terms_invalid");
    }

    [Fact]
    public void Prompts_count_mismatch_throws()
    {
        var plan = DefaultPlan();
        var shortPrompts = Prompts(plan).Take(plan.Terms.Count - 1).ToList();

        var failure = Should.Throw<ArgumentException>(() => Build(
            Params(plan: plan), Story(), plan, LandscapePreset(), track: null, prompts: shortPrompts));

        failure.ParamName.ShouldBe("prompts");
        failure.Message.ShouldContain($"{plan.Terms.Count} terms", Case.Sensitive);
        failure.Message.ShouldContain($"{shortPrompts.Count} prompts", Case.Sensitive);
    }

    [Fact]
    public void Validation_collects_the_first_failing_rule_only()
    {
        var style = Style();
        var emptyPlan = Plan([]);
        var badScript = new string('a', RenderSubmitMapper.MaxScriptRunes + 1);
        var badTitle = new string('t', RenderSubmitMapper.MaxSubjectLength + 1);

        FirstCode(Params(aspect: "square", plan: emptyPlan), Story(body: badScript, title: badTitle), emptyPlan, style)
            .ShouldBe("render.aspect_invalid");
        FirstCode(Params(plan: emptyPlan), Story(body: badScript, title: badTitle), emptyPlan, style)
            .ShouldBe("render.subject_invalid");
        FirstCode(Params(plan: emptyPlan), Story(body: badScript), emptyPlan, style)
            .ShouldBe("render.script_too_long");
        FirstCode(Params(plan: emptyPlan), Story(), emptyPlan, style)
            .ShouldBe("render.terms_invalid");
    }

    [Fact]
    public void Script_empty_returns_script_too_long()
    {
        // D3 requires 1-20,000 code points but names no code for a short body; the closed mapper-error list of
        // the 05.1 plan attaches the whole 1-20,000 range to `render.script_too_long` (05.1.2-T1 gap report: a
        // dedicated code would need a 09 §5.4 / 05 §5.1 change).
        var built = Build(
            Params(), Story(body: string.Empty), DefaultPlan(), LandscapePreset(), track: null);

        built.IsError.ShouldBeTrue();
        built.FirstError.Code.ShouldBe("render.script_too_long");
    }

    [Fact]
    public void Music_without_track_throws()
    {
        var p = Params(music: Choice());

        var failure = Should.Throw<ArgumentException>(() => Build(
            p, Story(), DefaultPlan(), LandscapePreset(), track: null));

        failure.ParamName.ShouldBe("track");
    }

    [Fact]
    public void Music_with_a_different_track_throws()
    {
        var p = Params(music: Choice());

        var failure = Should.Throw<ArgumentException>(() => Build(
            p, Story(), DefaultPlan(), LandscapePreset(), Track(trackId: "another-track")));

        failure.ParamName.ShouldBe("track");
        failure.Message.ShouldContain("another-track", Case.Sensitive);
    }

    [Fact]
    public void Music_null_with_a_track_returns_no_music()
    {
        var built = Submit(Params(), Story(), DefaultPlan(), LandscapePreset(), track: Track());

        built.Mpt.BgmType.ShouldBe(string.Empty);
        built.Mpt.BgmFile.ShouldBe(string.Empty);
        built.Mpt.BgmVolume.ShouldBe(0m);
        Canonical(built).ShouldContain("\"bgm_volume\": 0", Case.Sensitive);
    }

    [Fact]
    public void Mapper_error_codes_are_the_contract_codes()
    {
        RenderSubmitMapper.AspectInvalid.ShouldBe("render.aspect_invalid");
        RenderSubmitMapper.SubjectInvalid.ShouldBe("render.subject_invalid");
        RenderSubmitMapper.ScriptTooLong.ShouldBe("render.script_too_long");
        RenderSubmitMapper.TermsInvalid.ShouldBe("render.terms_invalid");
    }

    [Fact]
    public void Image_plan_summary_and_image_plan_json_agree()
    {
        var plan = new RenderImagePlan(
            [new PlannedTerm(0, "hook", "keyword one"), new PlannedTerm(1, "p:1", "keyword two")],
            clipSeconds: 10,
            neededImages: 3,
            maxTerms: 4,
            trimmedCount: 1,
            hintCodes: ["scenes_trimmed", "scenes_sparse"]);

        var summaryJson = JsonSerializer.Serialize(ImagePlanSummary.From(plan, StylePromptVersion), RenderNodeJson.Strict);
        var plannedJson = ImagePlanJson.Serialize(plan, StylePromptVersion);

        JsonNode.DeepEquals(JsonNode.Parse(summaryJson), JsonNode.Parse(plannedJson)).ShouldBeTrue(
            $"the two writers of image_plan must not drift:\nsummary: {summaryJson}\nplanner: {plannedJson}");
    }

    // ------------------------------------------------------------------ BuildParams (05 D11) -------------------

    [Fact]
    public void BuildParams_music_null_when_no_choice()
    {
        var withoutMusic = Params();

        withoutMusic.Music.ShouldBeNull();

        var withMusic = Params(music: Choice(), musicAttribution: "Fixture Composer — CC BY 4.0");

        withMusic.Music!.TrackId.ShouldBe("dark-ambient-01");
        withMusic.Music.BgmVolume.ShouldBe(0.146m);
        withMusic.Music.Attribution.ShouldBe("Fixture Composer — CC BY 4.0");
    }

    [Fact]
    public void BuildParams_preset_is_aspect_at_revision()
    {
        // BuildParams copies the SubtitlesParams its caller built: 09.6.1-T1 composes "<aspect>@<revision>"
        // from the preset catalog's revision (05 D6/D11) before calling, because the mapper loads no catalog
        // (no I/O). 05.1.2-T1 gap report: the task's Design line gives that composition to BuildParams itself,
        // but RenderParamsInput carries no revision.
        var p = RenderSubmitMapper.BuildParams(new RenderParamsInput(
            StoryVersionId,
            "portrait",
            "en",
            "Ngọc Huyền",
            StyleTemplateId,
            StylePromptVersion,
            ImagePlanSummary.From(DefaultPlan(), StylePromptVersion),
            new SubtitlesParams(true, "portrait@1"),
            null,
            null,
            10));

        p.Subtitles.Preset.ShouldBe("portrait@1");
        p.Subtitles.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void BuildParams_clip_seconds_is_ten()
    {
        Params().ClipSeconds.ShouldBe(10);

        var p = RenderSubmitMapper.BuildParams(new RenderParamsInput(
            StoryVersionId,
            "landscape",
            "vi",
            "Hải Đăng",
            StyleTemplateId,
            StylePromptVersion,
            ImagePlanSummary.From(DefaultPlan(), StylePromptVersion),
            new SubtitlesParams(true, "landscape@1"),
            null,
            null,
            12));

        p.ClipSeconds.ShouldBe(12, "ClipSeconds is copied from the input, never hardcoded by BuildParams");

        // The wire value is the fixed D3 literal, so it never follows the stored params.
        Submit(p, Story(), DefaultPlan(), LandscapePreset(), track: null)
            .Mpt.VideoClipDuration.ShouldBe(10);
    }

    [Fact]
    public void BuildParams_round_trips_with_unmapped_members_disallowed()
    {
        var input = new RenderParamsInput(
            StoryVersionId,
            "portrait",
            "en",
            "Ngọc Huyền",
            StyleTemplateId,
            StylePromptVersion,
            ImagePlanSummary.From(DefaultPlan(), StylePromptVersion),
            new SubtitlesParams(true, "portrait@1"),
            Choice(),
            "Fixture Composer — CC BY 4.0",
            10);

        var p = RenderSubmitMapper.BuildParams(input);

        p.Schema.ShouldBe("render-params.v1");
        p.MapperVersion.ShouldBe(1);
        p.StoryVersionId.ShouldBe(input.StoryVersionId);
        p.Aspect.ShouldBe(input.Aspect);
        p.Language.ShouldBe(input.Language);
        p.VoiceId.ShouldBe(input.VoiceId);
        p.StyleTemplateId.ShouldBe(input.StyleTemplateId);
        p.StylePromptVersion.ShouldBe(input.StylePromptVersion);
        p.ImagePlan.ShouldBeSameAs(input.ImagePlan, "BuildParams stores the caller's summary verbatim");
        p.Subtitles.ShouldBe(input.Subtitles);
        p.Music!.TrackId.ShouldBe(input.Music!.TrackId);
        p.Music.BgmVolume.ShouldBe(input.Music.BgmVolume);
        p.Music.Attribution.ShouldBe(input.MusicAttribution);

        // The image plan's hints and slot segments are IReadOnlyList members, so the generated record equality
        // compares them by reference: the reload is compared field by field instead.
        var json = JsonSerializer.Serialize(p, RenderNodeJson.Strict);
        var reloaded = JsonSerializer.Deserialize<RenderParamsV1>(json, RenderNodeJson.Strict)
            ?? throw new InvalidOperationException("render-params.v1 deserialised to null");

        reloaded.Schema.ShouldBe(p.Schema);
        reloaded.MapperVersion.ShouldBe(p.MapperVersion);
        reloaded.StoryVersionId.ShouldBe(p.StoryVersionId);
        reloaded.Aspect.ShouldBe(p.Aspect);
        reloaded.Language.ShouldBe(p.Language);
        reloaded.VoiceId.ShouldBe(p.VoiceId);
        reloaded.StyleTemplateId.ShouldBe(p.StyleTemplateId);
        reloaded.StylePromptVersion.ShouldBe(p.StylePromptVersion);
        reloaded.ClipSeconds.ShouldBe(p.ClipSeconds);
        reloaded.Subtitles.ShouldBe(p.Subtitles);
        reloaded.Music.ShouldBe(p.Music);
        reloaded.ImagePlan.ClipSeconds.ShouldBe(p.ImagePlan.ClipSeconds);
        reloaded.ImagePlan.Needed.ShouldBe(p.ImagePlan.Needed);
        reloaded.ImagePlan.MaxTerms.ShouldBe(p.ImagePlan.MaxTerms);
        reloaded.ImagePlan.Planned.ShouldBe(p.ImagePlan.Planned);
        reloaded.ImagePlan.Trimmed.ShouldBe(p.ImagePlan.Trimmed);
        reloaded.ImagePlan.Hints.ShouldBe(p.ImagePlan.Hints);
        reloaded.ImagePlan.SlotSegments.ShouldBe(p.ImagePlan.SlotSegments);
        reloaded.ImagePlan.StylePromptVersion.ShouldBe(p.ImagePlan.StylePromptVersion);

        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
            .ShouldBe(D11Keys, ignoreOrder: true, customMessage: "the document is exactly the D11 key set");
        document.RootElement.GetProperty("schema").GetString().ShouldBe("render-params.v1");
        string[] expectedMusicKeys = ["track_id", "bgm_volume", "attribution"];
        document.RootElement.GetProperty("music").EnumerateObject().Select(property => property.Name).ToArray()
            .ShouldBe(expectedMusicKeys, ignoreOrder: true);
    }

    // ------------------------------------------------------------------ Helpers --------------------------------

    /// <summary>
    /// Pins one D14 fixture: the canonical writer reproduces its bytes and <c>BuildSubmit</c> — handed the
    /// fixture's own terms and hashes as the prepared prompts — reproduces the whole body byte for byte.
    /// </summary>
    private static void AssertMatchesGolden(string file)
    {
        var fixtureText = RenderFixtures.ReadJson(file);
        var fixture = ReadFixture<AdapterSubmitRequestV1>(file);

        var rewritten = Canonical(fixture);
        rewritten.ShouldBe(
            fixtureText,
            $"canonical JSON must reproduce '{file}' (names, order, number scale and raw UTF-8)."
            + $"\nexpected:\n{fixtureText}\nactual:\n{rewritten}");

        var (p, story, plan, prompts, preset, track) = GoldenInputs(fixture);
        var built = RenderSubmitMapper.BuildSubmit(RenderJobId, p, story, plan, prompts, preset, track);
        built.IsError.ShouldBeFalse(
            $"BuildSubmit must accept the inputs derived from '{file}': {Messages(built)}");

        var actualJson = Canonical(built.Value);
        actualJson.ShouldBe(
            fixtureText,
            $"BuildSubmit must reproduce '{file}' byte for byte."
            + $"\nexpected:\n{fixtureText}\nactual:\n{actualJson}");
    }

    /// <summary>The inputs of one fixture: every value is read from the fixture's own wire body (05 D3).</summary>
    private static (RenderParamsV1 Params, StoryRenderInput Story, RenderImagePlan Plan, IReadOnlyList<ScenePrompt> Prompts, SubtitlePreset Preset, MusicTrack? Track)
        GoldenInputs(AdapterSubmitRequestV1 fixture)
    {
        var mpt = fixture.Mpt;
        var landscape = mpt.VideoAspect == "16:9";
        var aspect = landscape ? "landscape" : "portrait";
        var language = mpt.VideoLanguage;
        mpt.VoiceName.ShouldStartWith(KokoroPrefix, Case.Sensitive);

        var keywords = Enumerable.Range(1, mpt.VideoTerms.Count)
            .Select(index => index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var plan = new RenderImagePlan(
            [.. keywords.Select((keyword, slot) => new PlannedTerm(slot, slot == 0 ? "hook" : $"p:{slot}", keyword))],
            clipSeconds: mpt.VideoClipDuration,
            neededImages: mpt.VideoTerms.Count,
            maxTerms: fixture.ImagePlan.MaxImages,
            trimmedCount: 0,
            hintCodes: []);

        var preset = new SubtitlePreset(
            aspect,
            mpt.FontName,
            mpt.FontSize,
            mpt.StrokeWidth,
            mpt.StrokeColor,
            mpt.TextForeColor,
            mpt.TextBackgroundColor,
            mpt.RoundedSubtitleBackground,
            mpt.SubtitlePosition,
            mpt.CustomPosition,
            landscape ? LandscapeMaxCharsPerLine : PortraitMaxCharsPerLine,
            PresetMaxLines);

        var music = mpt.BgmType == "custom"
            ? Choice(file: mpt.BgmFile, bgmVolume: mpt.BgmVolume)
            : null;
        var track = music is null ? null : Track(fileName: mpt.BgmFile);

        var p = RenderSubmitMapper.BuildParams(new RenderParamsInput(
            StoryVersionId,
            aspect,
            language,
            mpt.VoiceName[KokoroPrefix.Length..],
            StyleTemplateId,
            StylePromptVersion,
            ImagePlanSummary.From(plan, StylePromptVersion),
            new SubtitlesParams(mpt.SubtitleEnabled, aspect + "@1"),
            music,
            null,
            mpt.VideoClipDuration));

        // The prepared prompts are the fixture's own two arrays: the D14 placeholders are what 05.4.1-T2 wrote
        // (their hashes are the hashes of those very strings), so the golden case sends them back instead of
        // rebuilding them through the prompt builder. The fixture carries no moderation text, so the term
        // doubles as it — that member never reaches the wire.
        fixture.ImagePlan.PromptSha256.Count.ShouldBe(mpt.VideoTerms.Count, "one hash per term");
        var prompts = mpt.VideoTerms
            .Select((term, slot) => new ScenePrompt(term, term, fixture.ImagePlan.PromptSha256[slot]))
            .ToArray();

        return (p, Story(body: mpt.VideoScript, title: mpt.VideoSubject, language: language), plan, prompts, preset, track);
    }

    private static string FirstCode(RenderParamsV1 p, StoryRenderInput story, RenderImagePlan plan, StyleTemplate style)
    {
        var built = Build(
            p, story, plan, LandscapePreset(), track: null, prompts: Prompts(plan, style));

        built.IsError.ShouldBeTrue("this case must fail a D3 rule");

        return built.FirstError.Code;
    }

    /// <summary>
    /// Runs one submit. The prompts default to the production path — <c>BuildPrompts</c> over the passed plan
    /// and the test template — so a test that cares about the prompts' text or hashes passes
    /// <paramref name="prompts"/> explicitly.
    /// </summary>
    private static ErrorOr<AdapterSubmitRequestV1> Build(
        RenderParamsV1 p,
        StoryRenderInput story,
        RenderImagePlan plan,
        SubtitlePreset preset,
        MusicTrack? track,
        IReadOnlyList<ScenePrompt>? prompts = null) =>
        RenderSubmitMapper.BuildSubmit(
            RenderJobId, p, story, plan, prompts ?? Prompts(plan), preset, track);

    /// <summary>Runs one submit that must succeed.</summary>
    private static AdapterSubmitRequestV1 Submit(
        RenderParamsV1 p,
        StoryRenderInput story,
        RenderImagePlan plan,
        SubtitlePreset preset,
        MusicTrack? track,
        IReadOnlyList<ScenePrompt>? prompts = null)
    {
        var built = Build(p, story, plan, preset, track, prompts);
        built.IsError.ShouldBeFalse($"BuildSubmit must succeed for this case: {Messages(built)}");

        return built.Value;
    }

    private static IReadOnlyList<ScenePrompt> Prompts(RenderImagePlan plan, StyleTemplate? style = null) =>
        RenderSubmitMapper.BuildPrompts(plan, style ?? Style());

    private static string Messages<T>(ErrorOr<T> result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}"));

    /// <summary>The canonical writer of the golden comparison: 2-space indent, <c>\n</c> and raw UTF-8.</summary>
    private static JsonSerializerOptions CanonicalWriter() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The fixtures are LF whatever platform wrote them, so the writer never takes the platform newline.
        NewLine = "\n",
        // The fixtures carry raw "<" and Vietnamese letters; the default encoder would write "\u003C" and
        // "\u1EA3", which is the same JSON value but not the same bytes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Canonical<T>(T value) => JsonSerializer.Serialize(value, CanonicalWriter()) + "\n";

    private static T ReadFixture<T>(string file) =>
        JsonSerializer.Deserialize<T>(RenderFixtures.ReadJson(file), RenderNodeJson.Strict)
        ?? throw new InvalidOperationException($"'{file}' deserialised to null");

    private static IReadOnlyList<string> WireNames<T>() =>
        [.. typeof(T).GetProperties().Select(property =>
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)];

    private static StyleTemplate Style() => StyleTemplate.Create(
        "golden-test-style",
        "<styled scene prompt vi {term}> Ink-wash illustration of Vietnamese folklore, dense blacks, "
        + "paper grain, centred subject, muted colour palette, deliberate shadow shapes.");

    private static SubtitlePreset LandscapePreset() => new(
        "landscape", "BeVietnamPro-Bold.ttf", 84, 3.0m, "#000000", "#FFFFFF", false, false, "bottom", 70.0m,
        LandscapeMaxCharsPerLine, PresetMaxLines);

    private static SubtitlePreset PortraitPreset() => new(
        "portrait", "BeVietnamPro-Bold.ttf", 64, 3.0m, "#000000", "#FFFFFF", false, false, "custom", 62.0m,
        PortraitMaxCharsPerLine, PresetMaxLines);

    private static RenderImagePlan Plan(IReadOnlyList<string> keywords) => new(
        [.. keywords.Select((keyword, slot) => new PlannedTerm(slot, slot == 0 ? "hook" : $"p:{slot}", keyword))],
        clipSeconds: 10,
        neededImages: keywords.Count,
        maxTerms: keywords.Count,
        trimmedCount: 0,
        hintCodes: []);

    private static RenderImagePlan DefaultPlan() => Plan(["1", "2", "3", "4"]);

    private static StoryRenderInput Story(string body = "NTS_FIXTURE_SCRIPT", string title = "A fixture story title", string language = "vi") =>
        new(StoryVersionId, body, ["keyword one"], title, language, LengthTier.Standard, []);

    private static MusicChoice Choice(
        string trackId = "dark-ambient-01",
        string file = "dark-ambient-01.mp3",
        decimal bgmVolume = 0.146m) => new(trackId, file, bgmVolume);

    private static MusicTrack Track(string trackId = "dark-ambient-01", string fileName = "dark-ambient-01.mp3") =>
        MusicTrack.Create(
            trackId,
            fileName,
            "Dark Ambient 01",
            "Fixture Composer",
            "https://example.invalid/dark-ambient-01",
            MusicLicense.Cc0,
            null,
            MusicPlatformScope.Any,
            ["dark_ambient"],
            184.2m,
            -17.3m,
            0.146m,
            new string('a', 64),
            "music/" + fileName,
            catalogRevision: 1);

    private static RenderParamsV1 Params(
        string aspect = "landscape",
        string language = "vi",
        string voiceId = "Hải Đăng",
        MusicChoice? music = null,
        string? musicAttribution = null,
        bool subtitlesEnabled = true,
        string preset = "landscape@1",
        int clipSeconds = 10,
        RenderImagePlan? plan = null) =>
        RenderSubmitMapper.BuildParams(new RenderParamsInput(
            StoryVersionId,
            aspect,
            language,
            voiceId,
            StyleTemplateId,
            StylePromptVersion,
            ImagePlanSummary.From(plan ?? DefaultPlan(), StylePromptVersion),
            new SubtitlesParams(subtitlesEnabled, preset),
            music,
            musicAttribution,
            clipSeconds));
}
