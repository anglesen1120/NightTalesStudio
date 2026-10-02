using System.Text.Json.Nodes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The submit validator (05.4.1-T2 §3, 05 D3) through its public entry point: the frozen group order (key set →
/// types and ranges → fixed values), one reported group, and the <c>errors[].field</c>/<c>rule</c> pair the
/// envelope carries. Every valid D14 submit body must pass, which is what keeps the validator honest.
/// </summary>
public sealed class SubmitValidatorTests
{
    public static TheoryData<string> ValidFixtures => new()
    {
        SubmitBodies.LandscapeVi,
        SubmitBodies.PortraitEn,
        SubmitBodies.NoMusic,
    };

    public static TheoryData<string, string, string> KeySetViolations => new()
    {
        // An unknown key of the caller's own naming stays echoable …
        { "video_landscape", "1", "mpt.video_landscape" },
        // … a key with a digit, an uppercase letter or more than 64 characters does not (§10 T69).
        { "video_1", "1", "mpt.<redacted>" },
        { "VIDEO_SUBJECT", "1", "mpt.<redacted>" },
        { new string('k', 2048), "1", "mpt.<redacted>" },
    };

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void ValidFixture_IsAccepted(string fileName)
    {
        var result = SubmitValidator.Validate(Utf8(SubmitBodies.From(Guid.CreateVersion7(), fileName)));

        result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.Mpt.VideoSubject.ShouldNotBeNullOrWhiteSpace();
        result.Value.ImagePlan.MaxImages.ShouldBeGreaterThan(0);
    }

    [Theory]
    [MemberData(nameof(KeySetViolations))]
    public void UnknownKey_IsReportedAgainstItsField(string key, string value, string expectedField)
    {
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), key, value);

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.UnknownKey);
        result.FirstError.Description.ShouldBe(expectedField);
        // The 3 KB key never comes back: the field is redacted and the body stays small (§10 T69).
        result.FirstError.Description.Length.ShouldBeLessThan(128);
    }

    [Fact]
    public void MissingKey_IsReported()
    {
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), "font_size", null, remove: true);

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.MissingKey);
        result.FirstError.Description.ShouldBe("mpt.font_size");
    }

    [Fact]
    public void WrongType_IsReportedWithTheField()
    {
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), "font_size", "84");

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.WrongType);
        result.FirstError.Description.ShouldBe("mpt.font_size");
    }

    [Theory]
    [InlineData("video_fit_mode", "contain", "mpt.video_fit_mode")]
    [InlineData("video_count", 2, "mpt.video_count")]
    [InlineData("voice_volume", 0.5, "mpt.voice_volume")]
    [InlineData("custom_audio_file", "/var/lib/nts/mpt/storage/tasks/x/audio.mp3", "mpt.custom_audio_file")]
    [InlineData("video_concat_mode", "random", "mpt.video_concat_mode")]
    public void FixedValueViolated_IsReportedWithTheField(string key, object value, string expectedField)
    {
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), key, System.Text.Json.Nodes.JsonValue.Create(value));

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.FixedValue);
        result.FirstError.Description.ShouldBe(expectedField);
    }

    [Theory]
    [InlineData("font_size", 12, "mpt.font_size")]
    [InlineData("custom_position", 101.0, "mpt.custom_position")]
    [InlineData("stroke_width", 11.0, "mpt.stroke_width")]
    [InlineData("stroke_color", "#fffff", "mpt.stroke_color")]
    [InlineData("subtitle_position", "middle", "mpt.subtitle_position")]
    [InlineData("video_aspect", "4:3", "mpt.video_aspect")]
    [InlineData("video_language", "fr", "mpt.video_language")]
    [InlineData("voice_name", "Hải Đăng", "mpt.voice_name")]
    [InlineData("bgm_volume", 2.0, "mpt.bgm_volume")]
    [InlineData("bgm_type", "library", "mpt.bgm_type")]
    public void OutOfRange_IsReportedWithTheField(string key, object value, string expectedField)
    {
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), key, System.Text.Json.Nodes.JsonValue.Create(value));

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.OutOfRange);
        result.FirstError.Description.ShouldBe(expectedField);
    }

    [Fact]
    public void FirstFailingGroupOnly()
    {
        // A missing key (group 1) plus a broken fixed value (group 3): only the key set is reported.
        var missingKey = SubmitBodies.WithMpt(Guid.CreateVersion7(), "font_name", null, remove: true);
        missingKey = missingKey.Replace("\"video_count\":1", "\"video_count\":2", StringComparison.Ordinal);
        missingKey = missingKey.Replace("\"video_count\": 1", "\"video_count\": 2", StringComparison.Ordinal);

        var keyResult = SubmitValidator.Validate(Utf8(missingKey));
        keyResult.IsError.ShouldBeTrue();
        keyResult.Errors.Count.ShouldBe(1);
        keyResult.FirstError.Code.ShouldBe(SubmitRules.MissingKey);

        // A wrong type (group 2) plus a broken fixed value (group 3): only the type is reported.
        var wrongType = SubmitBodies.WithMpt(Guid.CreateVersion7(), "font_size", "84");
        wrongType = wrongType.Replace("\"video_count\":1", "\"video_count\":2", StringComparison.Ordinal);
        wrongType = wrongType.Replace("\"video_count\": 1", "\"video_count\": 2", StringComparison.Ordinal);

        var typeResult = SubmitValidator.Validate(Utf8(wrongType));
        typeResult.IsError.ShouldBeTrue();
        typeResult.FirstError.Code.ShouldBe(SubmitRules.WrongType);
    }

    [Theory]
    [InlineData("""{"schema":"render-submit.v1","render_job_id":"0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8e9f"}""", "body")]
    [InlineData("not json at all", "body")]
    [InlineData("", "body")]
    [InlineData("""{"schema":"render-submit.v1","render_job_id":"0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8e9f","render_job_id":"0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8e9f","mpt":{},"image_plan":{}}""", "body.render_job_id")]
    public void MalformedBody_IsRequestInvalid(string body, string expectedField)
    {
        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.RequestInvalid);
        result.FirstError.Description.ShouldBe(expectedField);
    }

    [Fact]
    public void UnknownTopLevelMember_IsRequestInvalid()
    {
        var body = SubmitBodies.From(Guid.CreateVersion7())
            .Replace("\"schema\":\"render-submit.v1\"", "\"schema\":\"render-submit.v1\",\"extra\":true", StringComparison.Ordinal);

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.RequestInvalid);
    }

    [Fact]
    public void WrongSchema_IsAFixedValueViolation()
    {
        var body = SubmitBodies.From(Guid.CreateVersion7())
            .Replace("render-submit.v1", "render-submit.v2", StringComparison.Ordinal);

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.FixedValue);
        result.FirstError.Description.ShouldBe("schema");
    }

    [Fact]
    public void ImagePlanViolations_AreReportedAgainstTheImagePlanFields()
    {
        var body = SubmitBodies.From(Guid.CreateVersion7())
            .Replace("\"max_images\":4", "\"max_images\":0", StringComparison.Ordinal);
        var maxImages = SubmitValidator.Validate(Utf8(body));
        maxImages.IsError.ShouldBeTrue();
        maxImages.FirstError.Description.ShouldBe("image_plan.max_images");

        var badHash = SubmitBodies.WithMpt(Guid.CreateVersion7(), "font_size", 84);
        badHash = badHash.Replace("\"prompt_sha256\":[\"4025b1d790ceddb2eb7832b96aa2d81bab6d49d4d9ebcf903012c912c169f7ff\"", "\"prompt_sha256\":[\"NOTAHASH\"", StringComparison.Ordinal);
        var hashResult = SubmitValidator.Validate(Utf8(badHash));
        hashResult.IsError.ShouldBeTrue();
        hashResult.FirstError.Description.ShouldBe("image_plan.prompt_sha256");
    }

    [Theory]
    [InlineData("video_subject", "mpt.video_subject")]
    [InlineData("video_terms", "mpt.video_terms")]
    [InlineData("font_name", "mpt.font_name")]
    public void NullMptMember_IsWrongType_NotA500(string key, string expectedField)
    {
        // `[JsonRequired]` only requires the key: a present-but-null member must be a group-2 type violation,
        // never the NullReferenceException the pipeline used to turn into a 500 (CR-0).
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), key, value: null);

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.WrongType);
        result.FirstError.Description.ShouldBe(expectedField);
    }

    [Theory]
    [InlineData("video_terms", "[null]", "mpt.video_terms")]
    [InlineData("prompt_sha256", "[null]", "image_plan.prompt_sha256")]
    public void NullCollectionElement_IsWrongType_NotA500(string key, string json, string expectedField)
    {
        var body = key == "prompt_sha256"
            ? SubmitBodies.WithImagePlan(Guid.CreateVersion7(), key, JsonNode.Parse(json))
            : SubmitBodies.WithMpt(Guid.CreateVersion7(), key, JsonNode.Parse(json));

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.WrongType);
        result.FirstError.Description.ShouldBe(expectedField);
    }

    [Fact]
    public void NullImagePlanMember_IsWrongType_NotA500()
    {
        var body = SubmitBodies.WithImagePlan(Guid.CreateVersion7(), "prompt_sha256", value: null);

        var result = SubmitValidator.Validate(Utf8(body));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubmitRules.WrongType);
        result.FirstError.Description.ShouldBe("image_plan.prompt_sha256");
    }

    [Fact]
    public void KeySet_MatchesTheWireTypes()
    {
        // The validator's key set is written out in 05 D3 order on purpose (group 1 needs the table's order);
        // this keeps it equal to the wire type's declared members, so a field added to the DTO cannot be
        // forgotten in the validator or vice versa.
        WireKeys<MptSubmitFields>().ShouldBe(SubmitValidator.MptKeys);
        WireKeys<ImagePlanV1>().ShouldBe(SubmitValidator.ImagePlanKeys);
    }

    private static IReadOnlyList<string> WireKeys<T>() =>
    [
        .. typeof(T)
            .GetProperties()
            .Select(property => property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), inherit: false)
                .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                .Single()
                .Name),
    ];

    private static byte[] Utf8(string body) => System.Text.Encoding.UTF8.GetBytes(body);
}
