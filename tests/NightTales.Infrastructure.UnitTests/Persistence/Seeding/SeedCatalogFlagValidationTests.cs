using NightTales.Infrastructure.Persistence.Seeding;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence.Seeding;

/// <summary>
/// The <c>featureFlags</c> parser of <see cref="SeedCatalogFile"/> (09.2.5-T1 acceptance: "duplicate
/// keys in the file → the seed fails with the key named"). It fails before any write, so a bad catalog
/// aborts <c>nts-seed</c> with a key-named code. The <c>voices</c> half moved to
/// <c>VoiceCatalogRulesTests</c> with 04.2.2-T2 (04 README D9, CR-9): the dev catalog no longer carries
/// voices.
/// </summary>
public sealed class SeedCatalogFlagValidationTests
{
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
