using System.Text;
using System.Text.Json;
using ErrorOr;
using NightTales.Domain.Catalog;
using NightTales.Infrastructure.Persistence.Seeding;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Catalog;

/// <summary>
/// The shared voice-catalog fixtures of 04 README D14, read through the production reader and rules.
/// The same table (<c>expected.json</c>) drives the proxy's pytest suite, so a divergence between the
/// C# rules and the Python loader shows up as a failing fixture here instead of as a catalog that
/// loads on the proxy and fails to seed in production.
/// </summary>
public sealed class VoiceCatalogRulesTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", "voices");

    private static readonly string SeedFile = Path.Combine(AppContext.BaseDirectory, "seed", "voices.v1.json");

    public static TheoryData<string> ValidFixtureFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in FixtureFiles())
        {
            if (Expectations()[file].Code is null)
            {
                data.Add(file);
            }
        }

        return data;
    }

    public static TheoryData<string> InvalidFixtureFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in FixtureFiles())
        {
            if (Expectations()[file].Code is not null)
            {
                data.Add(file);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ValidFixtureFiles))]
    public void ValidFixtures_Validate(string file)
    {
        ValidateFixture(file).IsError.ShouldBeFalse($"'{file}' is a valid voices.v1 document");
    }

    [Theory]
    [MemberData(nameof(InvalidFixtureFiles))]
    public void InvalidFixtures_FailWithExpectedCodeAndVoiceId(string file)
    {
        var expected = Expectations()[file];

        var validation = ValidateFixture(file);

        validation.IsError.ShouldBeTrue($"'{file}' must be rejected");
        validation.FirstError.Code.ShouldBe(expected.Code);
        VoiceIdOf(validation.FirstError).ShouldBe(expected.VoiceId);
    }

    [Fact]
    public void EveryFixtureFile_HasExpectation()
    {
        var expected = Expectations();

        foreach (var file in FixtureFiles())
        {
            expected.ContainsKey(file).ShouldBeTrue($"'{file}' must have an expectation in expected.json");
        }

        expected.Count.ShouldBe(FixtureFiles().Count);
    }

    [Fact]
    public void SeedFile_IsValidWithRevisionAtLeast1()
    {
        var loaded = VoiceCatalogFileReader.Load(File.ReadAllBytes(SeedFile));

        loaded.IsError.ShouldBeFalse("the committed seed/voices.v1.json must load");
        loaded.Value.Schema.ShouldBe(VoiceCatalogRules.Schema);
        loaded.Value.Revision.ShouldBeGreaterThanOrEqualTo(1);
        loaded.Value.Voices.ShouldNotBeEmpty();
    }

    [Fact]
    public void Reader_AcceptsRevisionBeyondInt32()
    {
        // The proxy reads the revision with Python's unbounded int, so the C# reader must not invent an
        // Int32 bound (QA-0422T2-4); the ≥ 1 active voice per language rule still applies.
        const string json = """
            {
              "schema": "voices.v1",
              "revision": 8589934592,
              "vieneuCommit": "c1390abbdb2eedcdf58eafb546966c06ce27af71",
              "voices": [
                {
                  "id": "Hải Đăng",
                  "slug": "hai-dang",
                  "displayName": "Hải Đăng",
                  "language": "vi",
                  "gender": "male",
                  "tags": [],
                  "active": true,
                  "previewStorageKey": null
                },
                {
                  "id": "Ngọc Huyền",
                  "slug": "ngoc-huyen",
                  "displayName": "Ngọc Huyền",
                  "language": "en",
                  "gender": "female",
                  "tags": [],
                  "active": true,
                  "previewStorageKey": null
                }
              ]
            }
            """;

        VoiceCatalogFileReader.Load(Encoding.UTF8.GetBytes(json)).IsError.ShouldBeFalse();
    }

    /// <summary>The fixture files of <c>seed/fixtures/voices/</c>, without the expectation table itself.</summary>
    private static IReadOnlyList<string> FixtureFiles() =>
        [.. Directory.GetFiles(FixtureDirectory, "*.json")
            .Select(Path.GetFileName)
            .Where(name => name is not null && !string.Equals(name, "expected.json", StringComparison.Ordinal))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)];

    /// <summary>The parsed expectation table: <c>file → { code, voiceId }</c>.</summary>
    private static Dictionary<string, (string? Code, string? VoiceId)> Expectations()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureDirectory, "expected.json")));
        var expectations = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            expectations[property.Name] = (
                property.Value.TryGetProperty("code", out var code) ? code.GetString() : null,
                property.Value.TryGetProperty("voiceId", out var voiceId) ? voiceId.GetString() : null);
        }

        return expectations;
    }

    /// <summary>The result of loading one fixture: an error when the catalog is not valid.</summary>
    private static ErrorOr<Success> ValidateFixture(string file)
    {
        var loaded = VoiceCatalogFileReader.Load(File.ReadAllBytes(Path.Combine(FixtureDirectory, file)));
        if (loaded.IsError)
        {
            return loaded.FirstError;
        }

        return Result.Success;
    }

    private static string? VoiceIdOf(Error error) =>
        error.Metadata is not null
        && error.Metadata.TryGetValue(VoiceCatalogRules.VoiceIdMetadataKey, out var voiceId)
        && voiceId is string id
            ? id
            : null;
}
