using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The <c>render-submit.v1</c> validator of 05.4.1-T2 §3 (05 D3), in the frozen order: the key set, then types
/// and ranges, then the fixed values. Only the first failing group is reported, with one
/// <c>errors[].field</c>/<c>rule</c> pair per violation and never a caller's value.
/// </summary>
/// <remarks>
/// The body is validated through a staged envelope whose <c>mpt</c> and <c>image_plan</c> blocks stay
/// <see cref="JsonElement"/>: that is what lets the key-set group run before the typed key set does, without a
/// second JSON parser. The staged parse itself uses <see cref="RenderNodeJson.Strict"/>, so a malformed body, a
/// duplicate property (11 README E18/T1) and an unknown or missing top-level member are one
/// <c>request_invalid</c> answer and nothing is stored. Failed <see cref="ErrorOr{TValue}"/> entries carry the
/// rule as their code and the field as their description, which is exactly the envelope's
/// <c>{field, rule}</c> pair.
/// </remarks>
public static class SubmitValidator
{
    public const int MaxSubjectLength = 200;
    public const int MaxScriptCodePoints = 20_000;
    public const int MaxTerms = 200;
    public const int MaxTermLength = 1_000;
    public const int MaxImages = 200;
    public const int Sha256HexLength = 64;

    /// <summary>The <c>mpt</c> key set of 05 D3 in table order (everything except <c>n_threads</c>, F22).</summary>
    public static readonly IReadOnlyList<string> MptKeys =
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

    /// <summary>The <c>image_plan</c> key set of 03 D4.</summary>
    public static readonly IReadOnlyList<string> ImagePlanKeys = ["max_images", "prompt_sha256"];

    private const string MptField = "mpt";
    private const string ImagePlanField = "image_plan";
    private const string SchemaField = "schema";
    private const int MaxVoiceNameLength = 200;
    private const string VoiceNamePrefix = "kokoro:";

    private static readonly HashSet<string> MptKeySet = new(MptKeys, StringComparer.Ordinal);
    private static readonly HashSet<string> ImagePlanKeySet = new(ImagePlanKeys, StringComparer.Ordinal);
    private static readonly HashSet<string> SubtitlePositions = new(StringComparer.Ordinal)
    {
        "bottom",
        "top",
        "center",
        "two_thirds_bottom",
        "custom",
    };

    /// <summary>
    /// Validates a candidate <c>render-submit.v1</c> body. A success carries the MPT field set and the image
    /// plan; an error carries one violation per member of the first failing group.
    /// </summary>
    public static ErrorOr<ValidatedSubmit> Validate(ReadOnlySpan<byte> utf8Json)
    {
        SubmitEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SubmitEnvelope>(utf8Json, RenderNodeJson.Strict);
        }
        catch (JsonException exception)
        {
            // Malformed JSON, a duplicate property, an unknown or a missing top-level member.
            return Error.Validation(
                SubmitRules.RequestInvalid,
                AdapterFieldNames.FromJsonPath(exception.Path, AdapterFieldNames.Body));
        }

        if (envelope is null)
        {
            return Error.Validation(SubmitRules.RequestInvalid, AdapterFieldNames.Body);
        }

        // Group 1 — the exact key set of `mpt` and `image_plan`.
        var violations = new List<Error>();
        if (!IsObject(envelope.Mpt, MptField, violations) || !IsObject(envelope.ImagePlan, ImagePlanField, violations))
        {
            return violations;
        }

        CollectKeySetViolations(violations, MptField, MptKeys, MptKeySet, envelope.Mpt);
        CollectKeySetViolations(violations, ImagePlanField, ImagePlanKeys, ImagePlanKeySet, envelope.ImagePlan);
        if (violations.Count > 0)
        {
            return violations;
        }

        // Group 2 — types (the typed materialisation) and ranges.
        MptSubmitFields? mpt;
        ImagePlanV1? imagePlan;
        try
        {
            mpt = envelope.Mpt.Deserialize<MptSubmitFields>(RenderNodeJson.Strict);
            imagePlan = envelope.ImagePlan.Deserialize<ImagePlanV1>(RenderNodeJson.Strict);
        }
        catch (JsonException exception)
        {
            return Error.Validation(
                SubmitRules.WrongType,
                AdapterFieldNames.FromJsonPath(exception.Path, MptField));
        }

        if (mpt is null || imagePlan is null)
        {
            return Error.Validation(SubmitRules.WrongType, MptField);
        }

        // A member that is present but null (and a null element of a collection) is a type violation, not a
        // range one: `[JsonRequired]` only requires the key, so the value is checked before it is dereferenced.
        CollectNullViolations(violations, mpt, imagePlan);
        if (violations.Count > 0)
        {
            return violations;
        }

        CollectRangeViolations(violations, mpt, imagePlan);
        if (violations.Count > 0)
        {
            return violations;
        }

        // Group 3 — the fixed literals of 05 D3.
        CollectFixedValueViolations(violations, envelope.Schema, mpt);
        return violations.Count > 0 ? violations : new ValidatedSubmit(envelope.RenderJobId, mpt, imagePlan);
    }

    private static bool IsObject(JsonElement block, string field, List<Error> violations)
    {
        if (block.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        violations.Add(Error.Validation(SubmitRules.WrongType, field));
        return false;
    }

    private static void CollectKeySetViolations(
        List<Error> violations,
        string parent,
        IReadOnlyList<string> keys,
        HashSet<string> keySet,
        JsonElement block)
    {
        foreach (var property in block.EnumerateObject())
        {
            if (!keySet.Contains(property.Name))
            {
                violations.Add(Error.Validation(SubmitRules.UnknownKey, AdapterFieldNames.For(parent, property.Name)));
            }
        }

        foreach (var key in keys)
        {
            if (!block.TryGetProperty(key, out _))
            {
                violations.Add(Error.Validation(SubmitRules.MissingKey, AdapterFieldNames.For(parent, key)));
            }
        }
    }

    /// <summary>
    /// Reports every present-but-null member and every null collection element of group 2 as <c>wrong_type</c>.
    /// The nullable members of the contract (<c>video_transition_mode</c>, <c>video_materials</c>,
    /// <c>custom_audio_file</c>) are deliberately not checked: null is their fixed literal.
    /// </summary>
    private static void CollectNullViolations(List<Error> violations, MptSubmitFields mpt, ImagePlanV1 imagePlan)
    {
        AddIfNull(violations, mpt.VideoSubject, "mpt.video_subject");
        AddIfNull(violations, mpt.VideoScript, "mpt.video_script");
        AddIfNull(violations, mpt.VideoTerms, "mpt.video_terms");
        AddIfNull(violations, mpt.VideoAspect, "mpt.video_aspect");
        AddIfNull(violations, mpt.VideoFitMode, "mpt.video_fit_mode");
        AddIfNull(violations, mpt.VideoConcatMode, "mpt.video_concat_mode");
        AddIfNull(violations, mpt.VideoSource, "mpt.video_source");
        AddIfNull(violations, mpt.VideoLanguage, "mpt.video_language");
        AddIfNull(violations, mpt.VoiceName, "mpt.voice_name");
        AddIfNull(violations, mpt.BgmType, "mpt.bgm_type");
        AddIfNull(violations, mpt.BgmFile, "mpt.bgm_file");
        AddIfNull(violations, mpt.VideoMusicPrompt, "mpt.video_music_prompt");
        AddIfNull(violations, mpt.SoniloBgmPrompt, "mpt.sonilo_bgm_prompt");
        AddIfNull(violations, mpt.SubtitleDisplayMode, "mpt.subtitle_display_mode");
        AddIfNull(violations, mpt.SubtitleAnimation, "mpt.subtitle_animation");
        AddIfNull(violations, mpt.SubtitlePosition, "mpt.subtitle_position");
        AddIfNull(violations, mpt.StrokeColor, "mpt.stroke_color");
        AddIfNull(violations, mpt.TextForeColor, "mpt.text_fore_color");
        AddIfNull(violations, mpt.FontName, "mpt.font_name");
        AddIfNull(violations, mpt.VideoScriptPrompt, "mpt.video_script_prompt");
        AddIfNull(violations, mpt.CustomSystemPrompt, "mpt.custom_system_prompt");
        AddIfAnyNullElement(violations, mpt.VideoTerms, "mpt.video_terms");
        AddIfNull(violations, imagePlan.PromptSha256, "image_plan.prompt_sha256");
        AddIfAnyNullElement(violations, imagePlan.PromptSha256, "image_plan.prompt_sha256");
    }

    private static void AddIfNull(List<Error> violations, object? value, string field)
    {
        if (value is null)
        {
            violations.Add(Error.Validation(SubmitRules.WrongType, field));
        }
    }

    private static void AddIfAnyNullElement(List<Error> violations, IReadOnlyList<string>? values, string field)
    {
        if (values is not null && values.Any(value => value is null))
        {
            violations.Add(Error.Validation(SubmitRules.WrongType, field));
        }
    }

    private static void CollectRangeViolations(List<Error> violations, MptSubmitFields mpt, ImagePlanV1 imagePlan)
    {
        AddIf(violations, mpt.VideoSubject.Length is < 1 or > MaxSubjectLength, "mpt.video_subject");
        AddIf(violations, CountCodePoints(mpt.VideoScript) is < 1 or > MaxScriptCodePoints, "mpt.video_script");
        AddIf(violations, mpt.VideoTerms.Count is < 1 or > MaxTerms, "mpt.video_terms");
        AddIf(
            violations,
            mpt.VideoTerms.Count <= MaxTerms
                && mpt.VideoTerms.Any(term => term.Length is < 1 or > MaxTermLength),
            "mpt.video_terms");
        AddIf(violations, mpt.VideoAspect is not ("16:9" or "9:16"), "mpt.video_aspect");
        AddIf(violations, mpt.VideoLanguage is not ("vi" or "en"), "mpt.video_language");
        AddIf(
            violations,
            mpt.VoiceName.Length is < 1 or > MaxVoiceNameLength
                || !mpt.VoiceName.StartsWith(VoiceNamePrefix, StringComparison.Ordinal),
            "mpt.voice_name");
        AddIf(violations, mpt.BgmType is not ("" or "custom"), "mpt.bgm_type");
        AddIf(violations, mpt.BgmType == "custom" && mpt.BgmFile.Length == 0, "mpt.bgm_file");
        AddIf(violations, mpt.BgmVolume is < 0m or > 1m, "mpt.bgm_volume");
        AddIf(violations, mpt.CustomPosition is < 0m or > 100m, "mpt.custom_position");
        AddIf(violations, mpt.FontSize is < 24 or > 160, "mpt.font_size");
        AddIf(violations, mpt.StrokeWidth is < 0m or > 10m, "mpt.stroke_width");
        AddIf(violations, !IsHexColor(mpt.StrokeColor), "mpt.stroke_color");
        AddIf(violations, !IsHexColor(mpt.TextForeColor), "mpt.text_fore_color");
        AddIf(violations, !SubtitlePositions.Contains(mpt.SubtitlePosition), "mpt.subtitle_position");
        AddIf(violations, imagePlan.MaxImages is < 1 or > MaxImages, "image_plan.max_images");
        AddIf(
            violations,
            imagePlan.PromptSha256.Count is < 1 or > MaxImages
                || imagePlan.PromptSha256.Any(hash => !IsSha256Hex(hash)),
            "image_plan.prompt_sha256");
    }

    private static void CollectFixedValueViolations(List<Error> violations, string schema, MptSubmitFields mpt)
    {
        AddIf(violations, !string.Equals(schema, RenderNodeSchemas.RenderSubmit, StringComparison.Ordinal), SchemaField, SubmitRules.FixedValue);
        AddIf(violations, !string.Equals(mpt.VideoFitMode, "cover", StringComparison.Ordinal), "mpt.video_fit_mode", SubmitRules.FixedValue);
        AddIf(violations, !string.Equals(mpt.VideoConcatMode, "sequential", StringComparison.Ordinal), "mpt.video_concat_mode", SubmitRules.FixedValue);
        AddIf(violations, mpt.VideoTransitionMode is not null, "mpt.video_transition_mode", SubmitRules.FixedValue);
        AddIf(violations, mpt.VideoClipDuration != 10, "mpt.video_clip_duration", SubmitRules.FixedValue);
        AddIf(violations, mpt.VideoClipSpeed != 1.0m, "mpt.video_clip_speed", SubmitRules.FixedValue);
        AddIf(violations, !mpt.MatchMaterialsToScript, "mpt.match_materials_to_script", SubmitRules.FixedValue);
        AddIf(violations, mpt.VideoCount != 1, "mpt.video_count", SubmitRules.FixedValue);
        AddIf(violations, !string.Equals(mpt.VideoSource, "openai_image", StringComparison.Ordinal), "mpt.video_source", SubmitRules.FixedValue);
        AddIf(violations, mpt.VideoMaterials is not null, "mpt.video_materials", SubmitRules.FixedValue);
        AddIf(violations, mpt.CustomAudioFile is not null, "mpt.custom_audio_file", SubmitRules.FixedValue);
        AddIf(violations, mpt.VoiceVolume != 1.0m, "mpt.voice_volume", SubmitRules.FixedValue);
        AddIf(violations, mpt.VoiceRate != 1.0m, "mpt.voice_rate", SubmitRules.FixedValue);
        AddIf(violations, mpt.VideoMusicPrompt.Length != 0, "mpt.video_music_prompt", SubmitRules.FixedValue);
        AddIf(violations, mpt.SoniloBgmPrompt.Length != 0, "mpt.sonilo_bgm_prompt", SubmitRules.FixedValue);
        AddIf(violations, !string.Equals(mpt.SubtitleDisplayMode, "sentence", StringComparison.Ordinal), "mpt.subtitle_display_mode", SubmitRules.FixedValue);
        AddIf(violations, !string.Equals(mpt.SubtitleAnimation, "none", StringComparison.Ordinal), "mpt.subtitle_animation", SubmitRules.FixedValue);
        AddIf(violations, !string.Equals(mpt.FontName, "BeVietnamPro-Bold.ttf", StringComparison.Ordinal), "mpt.font_name", SubmitRules.FixedValue);
        AddIf(violations, mpt.ParagraphNumber != 1, "mpt.paragraph_number", SubmitRules.FixedValue);
        AddIf(violations, mpt.VideoScriptPrompt.Length != 0, "mpt.video_script_prompt", SubmitRules.FixedValue);
        AddIf(violations, mpt.CustomSystemPrompt.Length != 0, "mpt.custom_system_prompt", SubmitRules.FixedValue);
    }

    private static void AddIf(List<Error> violations, bool violated, string field, string rule = SubmitRules.OutOfRange)
    {
        if (violated)
        {
            violations.Add(Error.Validation(rule, field));
        }
    }

    /// <summary>
    /// Counts Unicode code points (surrogate pairs count once), which is the unit 05 D3 gives for
    /// <c>video_script</c> because it is VieNeu's input limit (04 V1, CR-3).
    /// </summary>
    private static int CountCodePoints(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsSurrogatePair(value, index))
            {
                index++;
            }

            count++;
        }

        return count;
    }

    private static bool IsHexColor(string value)
    {
        if (value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsUpperHexDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A lowercase sha256 hex string: the fixtures, CR-3 and the relay's slot lookup all use lowercase, and an
    /// uppercase hash would silently never match a slot (03 D5), so it is rejected rather than normalised.
    /// </summary>
    private static bool IsSha256Hex(string value)
    {
        if (value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUpperHexDigit(char character) =>
        character is (>= '0' and <= '9') or (>= 'A' and <= 'F');

    /// <summary>
    /// The staged shape of a submit body: only the envelope's own members are typed, so the two checked blocks
    /// can be validated member by member before their key set is trusted.
    /// </summary>
    internal sealed record SubmitEnvelope(
        [property: JsonPropertyName("schema")][property: JsonRequired] string Schema,
        [property: JsonPropertyName("render_job_id")][property: JsonRequired] Guid RenderJobId,
        [property: JsonPropertyName("mpt")][property: JsonRequired] JsonElement Mpt,
        [property: JsonPropertyName("image_plan")][property: JsonRequired] JsonElement ImagePlan);
}
