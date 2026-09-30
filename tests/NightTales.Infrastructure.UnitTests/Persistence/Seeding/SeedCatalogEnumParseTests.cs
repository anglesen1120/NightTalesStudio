using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence.Seeding;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence.Seeding;

/// <summary>
/// The enum rule of <see cref="SeedCatalogFile"/> (review finding): a numeric string never parses into a
/// catalog enum. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would take the numeric
/// forms below as the enum's numeric value, so each must fail with the key-named code.
/// </summary>
public sealed class SeedCatalogEnumParseTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    public void Seed_NumericLanguage_FailsWithIdNamed(string language)
    {
        var json = $$"""{ "voices": [ { "id": "x", "displayName": "X", "language": "{{language}}", "gender": "female" } ] }""";

        Should.Throw<SeedValidationException>(() => SeedCatalogFile.ReadVoices(json))
            .Code.ShouldBe("seed_voice_invalid:x:invalid_language");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    public void Seed_NumericGender_FailsWithIdNamed(string gender)
    {
        var json = $$"""{ "voices": [ { "id": "x", "displayName": "X", "language": "vi", "gender": "{{gender}}" } ] }""";

        Should.Throw<SeedValidationException>(() => SeedCatalogFile.ReadVoices(json))
            .Code.ShouldBe("seed_voice_invalid:x:invalid_gender");
    }

    [Theory]
    [InlineData("vi", Language.Vi)]
    [InlineData("VI", Language.Vi)]
    [InlineData("en", Language.En)]
    public void Seed_AlphabeticLanguage_Parses(string language, Language expected)
    {
        var json = $$"""{ "voices": [ { "id": "x", "displayName": "X", "language": "{{language}}", "gender": "female" } ] }""";

        SeedCatalogFile.ReadVoices(json).Single().Language.ShouldBe(expected);
    }

    [Theory]
    [InlineData("true_crime", Topic.TrueCrime)]
    [InlineData("trueCrime", Topic.TrueCrime)]
    public void Seed_SnakeCaseTopicHint_Parses(string hint, Topic expected)
    {
        var json = $$"""{ "templates": [ { "name": "ok", "template": "A {term} at night.", "topicHint": "{{hint}}" } ] }""";

        SeedCatalogFile.ReadStyleTemplates(json).Single().TopicHint.ShouldBe(expected);
    }
}
