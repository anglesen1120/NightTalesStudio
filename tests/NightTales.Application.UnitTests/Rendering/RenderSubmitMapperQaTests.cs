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
/// Independent QA probes over <c>RenderSubmitMapper</c> (05.1.2-T1): the D3 key set transcribed by hand from
/// 05 README D3, the fixture's own internal consistency, culture-free determinism, the exact decimal scale of
/// the wire, the UTF-16/Rune unit asymmetry on the three length bounds, hostile input and NULL arguments.
/// These tests assert the behaviour observed on the implemented tree; anything they pin that looks wrong is
/// reported as a QA finding rather than silently accepted.
/// </summary>
public sealed class RenderSubmitMapperQaTests
{
    /// <summary>The D3 table of 05 README L150–L175, transcribed by hand in table order, minus <c>n_threads</c> (F22).</summary>
    private static readonly string[] D3WireNames =
    [
        "video_subject",
        "video_script",
        "video_terms",
        "video_aspect",
        "video_fit_mode",
        "video_concat_mode",
        "video_transition_mode",
        "video_clip_duration",
        "video_clip_speed",
        "match_materials_to_script",
        "video_count",
        "video_source",
        "video_materials",
        "custom_audio_file",
        "video_language",
        "voice_name",
        "voice_volume",
        "voice_rate",
        "bgm_type",
        "bgm_file",
        "bgm_volume",
        "video_music_prompt",
        "sonilo_bgm_prompt",
        "subtitle_enabled",
        "subtitle_display_mode",
        "subtitle_animation",
        "subtitle_position",
        "custom_position",
        "font_size",
        "stroke_color",
        "stroke_width",
        "text_fore_color",
        "text_background_color",
        "rounded_subtitle_background",
        "font_name",
        "paragraph_number",
        "video_script_prompt",
        "custom_system_prompt",
    ];

    private static readonly string[] GoldenFixtures =
    [
        "render-submit.v1.landscape-vi.json",
        "render-submit.v1.portrait-en.json",
        "render-submit.v1.no-music.json",
    ];

    private static readonly Guid RenderJobId = Guid.Parse("0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8e9f");

    private static readonly Guid StoryVersionId = Guid.Parse("0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8ea1");

    private static readonly Guid StyleTemplateId = Guid.Parse("0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8ea2");

    private const string StylePromptVersion = "style=golden-test-style@1a2b3c4d;neg=v1";

    // ------------------------------------------------------------------ Contract drift (D3 / D14) -----------

    [Fact]
    public void Mpt_wire_key_set_is_the_d3_table_in_table_order()
    {
        var declared = WireNames<MptSubmitFields>();

        declared.ShouldBe(D3WireNames, ignoreOrder: false, customMessage: "the mpt object is D3's table order, minus n_threads");
        declared.Count.ShouldBe(38);
    }

    [Theory]
    [InlineData("render-submit.v1.landscape-vi.json")]
    [InlineData("render-submit.v1.portrait-en.json")]
    [InlineData("render-submit.v1.no-music.json")]
    public void Fixture_mpt_key_set_and_order_match_the_d3_table(string file)
    {
        using var document = JsonDocument.Parse(RenderFixtures.ReadJson(file));
        var mpt = document.RootElement.GetProperty("mpt");
        var names = mpt.EnumerateObject().Select(property => property.Name).ToArray();

        names.ShouldBe(D3WireNames, ignoreOrder: false, customMessage: $"'{file}' must carry the D3 key set in D3 order");
    }

    [Theory]
    [InlineData("render-submit.v1.landscape-vi.json")]
    [InlineData("render-submit.v1.portrait-en.json")]
    [InlineData("render-submit.v1.no-music.json")]
    public void Fixture_prompt_sha256_is_the_sha256_of_its_own_term(string file)
    {
        // The golden case feeds each fixture its own terms and hashes back (05 CR-14), so nothing else checks
        // that the two arrays in the fixture agree. They must: the adapter's slot lookup is by prompt sha256.
        using var document = JsonDocument.Parse(RenderFixtures.ReadJson(file));
        var mpt = document.RootElement.GetProperty("mpt");
        var terms = mpt.GetProperty("video_terms").EnumerateArray().Select(term => term.GetString()!).ToArray();
        var hashes = document.RootElement.GetProperty("image_plan").GetProperty("prompt_sha256")
            .EnumerateArray().Select(hash => hash.GetString()!).ToArray();

        hashes.Length.ShouldBe(terms.Length, $"'{file}': one hash per term");

        for (var index = 0; index < terms.Length; index++)
        {
            ScenePromptBuilder.Sha256Hex(terms[index]).ShouldBe(hashes[index], $"'{file}' term {index}");
        }
    }

    [Theory]
    [InlineData("render-submit.v1.landscape-vi.json", "16:9", "bottom", 70.0)]
    [InlineData("render-submit.v1.portrait-en.json", "9:16", "custom", 62.0)]
    [InlineData("render-submit.v1.no-music.json", "16:9", "bottom", 70.0)]
    public void Fixture_carries_the_d3_fixed_literals_and_the_d6_preset(
        string file, string aspect, string position, double customPosition)
    {
        var fixture = ReadFixture(file);
        var mpt = fixture.Mpt;

        mpt.VideoAspect.ShouldBe(aspect);
        mpt.VideoFitMode.ShouldBe("cover");
        mpt.VideoConcatMode.ShouldBe("sequential");
        mpt.VideoTransitionMode.ShouldBeNull();
        mpt.VideoClipDuration.ShouldBe(10);
        mpt.VideoClipSpeed.ShouldBe(1.0m);
        mpt.MatchMaterialsToScript.ShouldBeTrue();
        mpt.VideoCount.ShouldBe(1);
        mpt.VideoSource.ShouldBe("openai_image");
        mpt.VideoMaterials.ShouldBeNull();
        mpt.CustomAudioFile.ShouldBeNull();
        mpt.VoiceVolume.ShouldBe(1.0m);
        mpt.VoiceRate.ShouldBe(1.0m);
        mpt.VideoMusicPrompt.ShouldBe(string.Empty);
        mpt.SoniloBgmPrompt.ShouldBe(string.Empty);
        mpt.SubtitleEnabled.ShouldBeTrue();
        mpt.SubtitleDisplayMode.ShouldBe("sentence");
        mpt.SubtitleAnimation.ShouldBe("none");
        mpt.SubtitlePosition.ShouldBe(position);
        mpt.CustomPosition.ShouldBe((decimal)customPosition);
        mpt.FontName.ShouldBe("BeVietnamPro-Bold.ttf");
        mpt.ParagraphNumber.ShouldBe(1);
        mpt.VideoScriptPrompt.ShouldBe(string.Empty);
        mpt.CustomSystemPrompt.ShouldBe(string.Empty);
    }

    [Fact]
    public void Serialized_body_carries_exactly_the_38_d3_keys_and_no_thread_member()
    {
        var built = Submit(Params(music: Choice(), musicAttribution: null), Story(), DefaultPlan(), LandscapePreset(), Track());

        using var document = JsonDocument.Parse(Canonical(built));
        var mptKeys = document.RootElement.GetProperty("mpt").EnumerateObject().Select(property => property.Name).ToArray();

        mptKeys.ShouldBe(D3WireNames, ignoreOrder: false);
        Canonical(built).ShouldNotContain("thread", Case.Insensitive);

        // D3 shapes: video_terms is an array (03 README I11 / F04), the three nullable fields are JSON null.
        var mpt = document.RootElement.GetProperty("mpt");
        mpt.GetProperty("video_terms").ValueKind.ShouldBe(JsonValueKind.Array);
        mpt.GetProperty("video_transition_mode").ValueKind.ShouldBe(JsonValueKind.Null);
        mpt.GetProperty("video_materials").ValueKind.ShouldBe(JsonValueKind.Null);
        mpt.GetProperty("custom_audio_file").ValueKind.ShouldBe(JsonValueKind.Null);
        typeof(MptSubmitFields).GetProperties()
            .Any(property => property.Name.Contains("Thread", StringComparison.OrdinalIgnoreCase)
                || (property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? string.Empty)
                    .Contains("thread", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse("F22: n_threads belongs to the adapter");
    }

    // ------------------------------------------------------------------ Determinism and number scale --------

    [Fact]
    public void Two_builds_are_byte_equal_and_culture_invariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var results = new List<string>();
            foreach (var culture in new[] { "de-DE", "vi-VN", "en-US" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                CultureInfo.CurrentUICulture = new CultureInfo(culture);
                results.Add(Canonical(Submit(Params(music: Choice()), Story(), DefaultPlan(), LandscapePreset(), Track())));
            }

            results[0].ShouldBe(results[1], "the body must not depend on the thread culture");
            results[0].ShouldBe(results[2]);
            results[0].ShouldNotContain("0,146", Case.Sensitive, "a comma decimal separator would corrupt the wire");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Decimal_scale_of_the_wire_is_preserved()
    {
        var built = Submit(Params(music: Choice(bgmVolume: 0.146m)), Story(), DefaultPlan(), LandscapePreset(), Track());
        var json = Canonical(built);

        json.ShouldContain("\"video_clip_speed\": 1.0", Case.Sensitive);
        json.ShouldContain("\"voice_volume\": 1.0", Case.Sensitive);
        json.ShouldContain("\"voice_rate\": 1.0", Case.Sensitive);
        json.ShouldContain("\"bgm_volume\": 0.146", Case.Sensitive);
        json.ShouldContain("\"custom_position\": 70.0", Case.Sensitive);
        json.ShouldContain("\"stroke_width\": 3.0", Case.Sensitive);

        // No music: the D3/D8 value is 0 with scale zero.
        var noMusic = Canonical(Submit(Params(), Story(), DefaultPlan(), LandscapePreset(), track: null));
        noMusic.ShouldContain("\"bgm_volume\": 0,", Case.Sensitive);
        noMusic.ShouldNotContain("\"bgm_volume\": 0.0", Case.Sensitive);
    }

    // ------------------------------------------------------------------ Length bounds and units --------------

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void Subject_at_both_bounds_is_accepted(int length)
    {
        var title = new string('t', length);

        Submit(Params(), Story(title: title), DefaultPlan(), LandscapePreset(), track: null)
            .Mpt.VideoSubject.ShouldBe(title);
    }

    [Fact]
    public void Subject_whitespace_only_is_accepted_and_sent_verbatim()
    {
        // QA observation: D3 sets only a 1-200 character range for video_subject, so "   " passes and reaches
        // MPT as the task title. Whether the approved story version can even carry such a title is 02's rule.
        var built = Submit(Params(), Story(title: "   "), DefaultPlan(), LandscapePreset(), track: null);

        built.Mpt.VideoSubject.ShouldBe("   ");

        Submit(Params(), Story(title: " "), DefaultPlan(), LandscapePreset(), track: null)
            .Mpt.VideoSubject.ShouldBe(" ");
    }

    [Fact]
    public void Subject_boundary_counts_utf16_units_while_the_script_counts_runes()
    {
        // QA observation: 'video_subject' is measured with string.Length (UTF-16 units) and 'video_script'
        // with EnumerateRunes (code points), although 05 D3 calls both a character range.
        var hundredEmoji = string.Concat(Enumerable.Repeat("\U0001F600", 100));
        hundredEmoji.Length.ShouldBe(200);

        Submit(Params(), Story(title: hundredEmoji), DefaultPlan(), LandscapePreset(), track: null)
            .Mpt.VideoSubject.ShouldBe(hundredEmoji);

        var hundredAndOne = hundredEmoji + "\U0001F600";
        hundredAndOne.Length.ShouldBe(202);

        var rejected = Build(Params(), Story(title: hundredAndOne), DefaultPlan(), LandscapePreset(), track: null);
        rejected.IsError.ShouldBeTrue();
        rejected.FirstError.Code.ShouldBe("render.subject_invalid", "101 astral characters are 100 code points but 202 UTF-16 units");
    }

    [Fact]
    public void Script_boundary_counts_runes_after_nfc_with_zwj_sequences()
    {
        // A ZWJ family is five code points and survives NFC unchanged: 4,000 of them are exactly 20,000 runes.
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        var atLimit = string.Concat(Enumerable.Repeat(family, 4_000));
        atLimit.Length.ShouldBe(32_000, "8 UTF-16 units per family, 5 runes");

        Submit(Params(), Story(body: atLimit), DefaultPlan(), LandscapePreset(), track: null)
            .Mpt.VideoScript.ShouldBe(atLimit);

        var overLimit = atLimit + family;
        var built = Build(Params(), Story(body: overLimit), DefaultPlan(), LandscapePreset(), track: null);
        built.IsError.ShouldBeTrue();
        built.FirstError.Code.ShouldBe("render.script_too_long");
    }

    [Fact]
    public void Script_nfc_composes_hangul_and_keeps_ligatures()
    {
        // Hangul jamo compose under NFC (4 -> 2 code points); a ligature is not decomposed by NFC.
        var jamo = "\u1100\u1161\u1100\u1161";
        jamo.Normalize(System.Text.NormalizationForm.FormC).Length.ShouldBe(2);

        Submit(Params(), Story(body: jamo), DefaultPlan(), LandscapePreset(), track: null)
            .Mpt.VideoScript.ShouldBe("\uAC00\uAC00");

        Submit(Params(), Story(body: "\uFB01nal"), DefaultPlan(), LandscapePreset(), track: null)
            .Mpt.VideoScript.ShouldBe("\uFB01nal", "NFC never decomposes the fi ligature");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void Terms_at_both_count_bounds_are_accepted(int count)
    {
        var plan = Plan([.. Enumerable.Range(0, count).Select(index => index.ToString(CultureInfo.InvariantCulture))]);
        var prompts = ShortPrompts(count);

        var built = Submit(Params(plan: plan), Story(), plan, LandscapePreset(), track: null, prompts: prompts);

        built.Mpt.VideoTerms.Count.ShouldBe(count);
        built.ImagePlan.PromptSha256.Count.ShouldBe(count);
    }

    [Fact]
    public void Term_length_boundary_is_measured_in_utf16_units()
    {
        // QA observation: the 1,000 limit of D3 is applied to string.Length, so 500 astral characters (1,000
        // UTF-16 units) pass while 501 (1,002 units, 501 code points) is rejected as terms_invalid.
        var plan = Plan(["one"]);
        var atLimit = ShortPrompts(1, string.Concat(Enumerable.Repeat("\U0001F600", 500)));

        Submit(Params(plan: plan), Story(), plan, LandscapePreset(), track: null, prompts: atLimit)
            .Mpt.VideoTerms[0].Length.ShouldBe(1_000);

        var overLimit = ShortPrompts(1, string.Concat(Enumerable.Repeat("\U0001F600", 501)));
        var built = Build(Params(plan: plan), Story(), plan, LandscapePreset(), track: null, prompts: overLimit);

        built.IsError.ShouldBeTrue();
        built.FirstError.Code.ShouldBe("render.terms_invalid");
    }

    // ------------------------------------------------------------------ Failure surface ----------------------

    [Theory]
    [InlineData("landscape ")]
    [InlineData("Landscape")]
    [InlineData("16:9")]
    [InlineData("")]
    public void Aspect_is_case_sensitive_and_never_defaults(string aspect)
    {
        var built = Build(Params(aspect: aspect), Story(), DefaultPlan(), LandscapePreset(), track: null);

        built.IsError.ShouldBeTrue();
        built.FirstError.Code.ShouldBe("render.aspect_invalid");
    }

    [Fact]
    public void Language_voice_and_subtitle_values_are_not_validated_by_the_mapper()
    {
        // QA observation (trust boundary): D3 fixes video_language to vi|en, but the mapper copies the stored
        // value. 09.6.1-T1 owns the series-language validation; the mapper must not silently rewrite it.
        var built = Submit(Params(language: "fr"), Story(language: "fr"), DefaultPlan(), LandscapePreset(), track: null);

        built.Mpt.VideoLanguage.ShouldBe("fr");
        built.Mpt.VoiceName.ShouldBe("kokoro:Hải Đăng");
    }

    [Fact]
    public void Preset_of_a_different_aspect_than_the_params_is_accepted()
    {
        // QA observation (integration risk): nothing cross-checks p.Aspect against preset.Aspect, so a caller
        // that loads the preset by the wrong aspect produces a body with mismatched canvas and subtitle values.
        var built = Submit(Params(aspect: "landscape", preset: "landscape@1"), Story(), DefaultPlan(), PortraitPreset(), track: null);

        built.Mpt.VideoAspect.ShouldBe("16:9");
        built.Mpt.SubtitlePosition.ShouldBe("custom");
        built.Mpt.FontSize.ShouldBe(64);
    }

    [Fact]
    public void BuildSubmit_null_arguments_throw_argument_null()
    {
        var p = Params();
        var story = Story();
        var plan = DefaultPlan();
        var prompts = ShortPrompts(plan.Terms.Count);
        var preset = LandscapePreset();

        Should.Throw<ArgumentNullException>(() => RenderSubmitMapper.BuildSubmit(RenderJobId, null!, story, plan, prompts, preset, null));
        Should.Throw<ArgumentNullException>(() => RenderSubmitMapper.BuildSubmit(RenderJobId, p, null!, plan, prompts, preset, null));
        Should.Throw<ArgumentNullException>(() => RenderSubmitMapper.BuildSubmit(RenderJobId, p, story, null!, prompts, preset, null));
        Should.Throw<ArgumentNullException>(() => RenderSubmitMapper.BuildSubmit(RenderJobId, p, story, plan, null!, preset, null));
        Should.Throw<ArgumentNullException>(() => RenderSubmitMapper.BuildSubmit(RenderJobId, p, story, plan, prompts, null!, null));

        // A null track is legal: it means "no music" (D8).
        Submit(p, story, plan, preset, track: null).Mpt.BgmType.ShouldBe(string.Empty);
    }

    [Fact]
    public void BuildPrompts_and_BuildParams_throw_on_null()
    {
        Should.Throw<ArgumentNullException>(() => RenderSubmitMapper.BuildPrompts(null!, Style()));
        Should.Throw<ArgumentNullException>(() => RenderSubmitMapper.BuildPrompts(DefaultPlan(), null!));
        Should.Throw<ArgumentNullException>(() => RenderSubmitMapper.BuildParams(null!));
    }

    // ------------------------------------------------------------------ render-params.v1 (D11) --------------

    [Fact]
    public void RenderParams_missing_required_member_is_rejected()
    {
        var json = JsonSerializer.Serialize(Params(), RenderNodeJson.Strict);

        Should.NotThrow(() => JsonSerializer.Deserialize<RenderParamsV1>(json, RenderNodeJson.Strict));

        foreach (var member in new[] { "mapper_version", "story_version_id", "aspect", "image_plan", "subtitles", "music", "clip_seconds" })
        {
            var without = RemoveMember(json, member);
            Should.Throw<JsonException>(
                () => JsonSerializer.Deserialize<RenderParamsV1>(without, RenderNodeJson.Strict),
                $"'{member}' is JsonRequired in D11");
        }
    }

    [Fact]
    public void RenderParams_schema_value_is_written_but_not_validated_on_read()
    {
        // QA observation: the DTO writes schema = render-params.v1 and marks it required, but a stored row
        // whose schema reads anything else still deserializes (the reader trusts render_jobs.params).
        var json = JsonSerializer.Serialize(Params(), RenderNodeJson.Strict).Replace("render-params.v1", "render-params.v9", StringComparison.Ordinal);

        var reloaded = JsonSerializer.Deserialize<RenderParamsV1>(json, RenderNodeJson.Strict);

        reloaded!.Schema.ShouldBe("render-params.v9");
    }

    [Fact]
    public void BuildParams_copies_every_stored_value_and_freezes_the_music()
    {
        var choice = Choice(bgmVolume: 0.5m);
        var p = RenderSubmitMapper.BuildParams(new RenderParamsInput(
            StoryVersionId, "portrait", "en", "Ngọc Huyền", StyleTemplateId, StylePromptVersion,
            ImagePlanSummary.From(DefaultPlan(), StylePromptVersion), new SubtitlesParams(true, "portrait@1"),
            choice, null, 10));

        p.Music!.BgmVolume.ShouldBe(0.5m);
        p.Music.Attribution.ShouldBeNull();

        // Music volume zero is a legal stored value and stays 0 with scale zero on the wire.
        var built = Submit(p, Story(), DefaultPlan(), PortraitPreset(), Track(bgmVolume: 0.5m));
        Canonical(built).ShouldContain("\"bgm_volume\": 0.5", Case.Sensitive);
    }

    // ------------------------------------------------------------------ Helpers -------------------------------

    private static string RemoveMember(string json, string member)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove(member).ShouldBeTrue($"'{member}' is present in the serialized document");

        return node.ToJsonString(CanonicalWriter());
    }

    private static T ReadFixture<T>(string file) =>
        JsonSerializer.Deserialize<T>(RenderFixtures.ReadJson(file), RenderNodeJson.Strict)
        ?? throw new InvalidOperationException($"'{file}' deserialised to null");

    private static AdapterSubmitRequestV1 ReadFixture(string file) => ReadFixture<AdapterSubmitRequestV1>(file);

    private static ErrorOr<AdapterSubmitRequestV1> Build(
        RenderParamsV1 p,
        StoryRenderInput story,
        RenderImagePlan plan,
        SubtitlePreset preset,
        MusicTrack? track,
        IReadOnlyList<ScenePrompt>? prompts = null) =>
        RenderSubmitMapper.BuildSubmit(RenderJobId, p, story, plan, prompts ?? ShortPrompts(plan.Terms.Count), preset, track);

    private static AdapterSubmitRequestV1 Submit(
        RenderParamsV1 p,
        StoryRenderInput story,
        RenderImagePlan plan,
        SubtitlePreset preset,
        MusicTrack? track,
        IReadOnlyList<ScenePrompt>? prompts = null)
    {
        var built = Build(p, story, plan, preset, track, prompts);
        built.IsError.ShouldBeFalse(string.Join("; ", built.Errors.Select(error => $"{error.Code}: {error.Description}")));

        return built.Value;
    }

    /// <summary>Short prompts (1,000-unit-safe by default) so a term-count boundary test does not trip the length rule.</summary>
    private static IReadOnlyList<ScenePrompt> ShortPrompts(int count, string? text = null)
    {
        return [.. Enumerable.Range(0, count).Select(slot =>
        {
            var body = text ?? $"scene prompt for the term #{slot}";
            return new ScenePrompt(body, body, ScenePromptBuilder.Sha256Hex(body));
        })];
    }

    private static JsonSerializerOptions CanonicalWriter() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Canonical<T>(T value) => JsonSerializer.Serialize(value, CanonicalWriter()) + "\n";

    private static IReadOnlyList<string> WireNames<T>() =>
        [.. typeof(T).GetProperties().Select(property =>
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)];

    private static StyleTemplate Style() => StyleTemplate.Create(
        "golden-test-style",
        "<styled scene prompt vi {term}> Ink-wash illustration of Vietnamese folklore, dense blacks, "
        + "paper grain, centred subject, muted colour palette, deliberate shadow shapes.");

    private static SubtitlePreset LandscapePreset() => new(
        "landscape", "BeVietnamPro-Bold.ttf", 84, 3.0m, "#000000", "#FFFFFF", false, false, "bottom", 70.0m, 42, 2);

    private static SubtitlePreset PortraitPreset() => new(
        "portrait", "BeVietnamPro-Bold.ttf", 64, 3.0m, "#000000", "#FFFFFF", false, false, "custom", 62.0m, 28, 2);

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

    private static MusicChoice Choice(string trackId = "dark-ambient-01", decimal bgmVolume = 0.146m) =>
        new(trackId, "dark-ambient-01.mp3", bgmVolume);

    private static MusicTrack Track(
        string trackId = "dark-ambient-01",
        string fileName = "dark-ambient-01.mp3",
        decimal bgmVolume = 0.146m) =>
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
            bgmVolume,
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
