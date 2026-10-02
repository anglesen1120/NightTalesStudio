using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence.Seeding;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence.Seeding;

/// <summary>
/// The enum rule of <see cref="SeedCatalogFile"/> (review finding): a numeric string never parses into a
/// catalog enum. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would take the numeric
/// forms below as the enum's numeric value, so each must fail with the key-named code. The voice
/// language/gender cases moved to <c>VoiceCatalogRulesTests</c> with 04.2.2-T2 (the voice catalog has
/// its own reader and rules now); the topic hint is the enum left in this parser.
/// </summary>
public sealed class SeedCatalogEnumParseTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    public void Seed_NumericTopicHint_FailsWithNameNamed(string hint)
    {
        var json = $$"""{ "templates": [ { "name": "ok", "template": "A {term} at night.", "topicHint": "{{hint}}" } ] }""";

        Should.Throw<SeedValidationException>(() => SeedCatalogFile.ReadStyleTemplates(json))
            .Code.ShouldBe("seed_style_template_invalid:ok:invalid_topic_hint");
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
