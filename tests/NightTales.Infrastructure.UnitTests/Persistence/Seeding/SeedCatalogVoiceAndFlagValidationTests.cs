using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence.Seeding;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence.Seeding;

/// <summary>
/// The <c>voices</c> and <c>featureFlags</c> parsers of <see cref="SeedCatalogFile"/> (09.2.5-T1
/// acceptance: "duplicate keys in the file → the seed fails with the key named"). They fail before any
/// write, so a bad catalog aborts <c>nts-seed</c> with a key-named code.
/// </summary>
public sealed class SeedCatalogVoiceAndFlagValidationTests
{
    [Fact]
    public void Seed_DuplicateVoiceId_FailsWithIdNamed()
    {
        const string duplicate = """
            { "voices": [
                { "id": "Mai Anh", "displayName": "Mai Anh", "language": "vi", "gender": "female" },
                { "id": "Mai Anh", "displayName": "Other", "language": "en", "gender": "male" } ] }
            """;

        var failure = Should.Throw<SeedValidationException>(() => SeedCatalogFile.ReadVoices(duplicate));
        failure.Code.ShouldBe("seed_voice_invalid:Mai Anh:duplicate_id");
    }

    [Fact]
    public void Seed_UnknownLanguageOrGender_FailsWithIdNamed()
    {
        const string badLanguage = """
            { "voices": [ { "id": "x", "displayName": "X", "language": "fr", "gender": "female" } ] }
            """;

        Should.Throw<SeedValidationException>(() => SeedCatalogFile.ReadVoices(badLanguage))
            .Code.ShouldBe("seed_voice_invalid:x:invalid_language");

        // A numeric string must not parse into the enum (Enum.TryParse would accept any number).
        const string numericLanguage = """
            { "voices": [ { "id": "x", "displayName": "X", "language": "1", "gender": "female" } ] }
            """;

        Should.Throw<SeedValidationException>(() => SeedCatalogFile.ReadVoices(numericLanguage))
            .Code.ShouldBe("seed_voice_invalid:x:invalid_language");

        const string badGender = """
            { "voices": [ { "id": "x", "displayName": "X", "language": "vi", "gender": "robot" } ] }
            """;

        Should.Throw<SeedValidationException>(() => SeedCatalogFile.ReadVoices(badGender))
            .Code.ShouldBe("seed_voice_invalid:x:invalid_gender");
    }

    [Fact]
    public void Seed_ValidVoice_ParsesEnumsTagsAndActive()
    {
        const string valid = """
            { "voices": [ { "id": "Mai Anh", "displayName": "Mai Anh", "language": "vi",
                            "gender": "female", "tags": ["news", "deep"], "active": false } ] }
            """;

        var row = SeedCatalogFile.ReadVoices(valid).Single();

        row.Id.ShouldBe("Mai Anh");
        row.Language.ShouldBe(Language.Vi);
        row.Gender.ShouldBe(VoiceGender.Female);
        row.Tags.ShouldBe(["news", "deep"]);
        row.Active.ShouldBeFalse();
    }

    [Fact]
    public void Seed_DuplicateOrInvalidFlagKey_FailsWithKeyNamed()
    {
        const string duplicate = """{ "featureFlags": ["youtube_publish", "youtube_publish"] }""";

        Should.Throw<SeedValidationException>(() => SeedCatalogFile.ReadFeatureFlags(duplicate))
            .Code.ShouldBe("seed_feature_flag_invalid:youtube_publish:duplicate_key");

        const string blank = """{ "featureFlags": ["  "] }""";

        Should.Throw<SeedValidationException>(() => SeedCatalogFile.ReadFeatureFlags(blank))
            .Code.ShouldBe("seed_feature_flag_invalid:-:invalid_key");
    }
}
