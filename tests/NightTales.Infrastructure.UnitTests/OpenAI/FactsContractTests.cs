using System.Text.Json;
using NightTales.Application.Features.Research.Facts;
using NightTales.Domain.Research;
using NightTales.Infrastructure.OpenAI;
using Shouldly;
using Xunit;

// The namespace carries `Research.Facts` on purpose, so `dotnet test --filter FullyQualifiedName~Research.Facts`
// (02.5.3-T1 acceptance) selects these tests beside the types they cover.
namespace NightTales.Infrastructure.UnitTests.Research.Facts;

/// <summary>
/// 02.5.3-T1: the <c>facts.v1</c> C# contract (snake_case, the <c>high|medium|low</c> confidence enum, the
/// strict-schema failures the adapter maps to <c>InvalidOutput</c>) and the <c>Facts</c> section's start-up
/// validation.
/// </summary>
public sealed class FactsContractTests
{
    /// <summary>The deserialiser the adapter uses (<c>OpenAiTextClient.OutputOptions</c>).</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    [Fact]
    public void FactsV1_DeserialisesSnakeCase_WithTheConfidenceEnum()
    {
        const string json = """
            {"facts":[{"statement":"A fact.","source_aliases":["s1","s2"],"confidence":"low"},
                       {"statement":"Another.","source_aliases":["s10"],"confidence":"high"}]}
            """;

        var facts = JsonSerializer.Deserialize<FactsV1>(json, Options).ShouldNotBeNull();

        facts.Facts.Count.ShouldBe(2);
        facts.Facts[0].Statement.ShouldBe("A fact.");
        facts.Facts[0].SourceAliases.ShouldBe(["s1", "s2"]);
        facts.Facts[0].Confidence.ShouldBe(FactConfidence.Low);
        facts.Facts[1].Confidence.ShouldBe(FactConfidence.High);
    }

    [Fact]
    public void FactsV1_EmptyFacts_IsValid()
    {
        JsonSerializer.Deserialize<FactsV1>("""{"facts":[]}""", Options).ShouldNotBeNull().Facts.ShouldBeEmpty();
    }

    [Fact]
    public void FactsV1_SerialisesTheClosedListWireNames()
    {
        var json = JsonSerializer.Serialize(new FactsV1([new FactDraftV1("s", ["s1"], FactConfidence.Medium)]), Options);

        json.ShouldContain("\"source_aliases\":[\"s1\"]");
        json.ShouldContain("\"confidence\":\"medium\"");
    }

    [Theory]
    [InlineData("""{"facts":[{"statement":"s","source_aliases":["s1"]}]}""")]                      // confidence missing
    [InlineData("""{"facts":[{"statement":"s","confidence":"high"}]}""")]                           // aliases missing
    [InlineData("""{"facts":[{"statement":"s","source_aliases":["s1"],"confidence":"certain"}]}""")] // outside the enum
    [InlineData("""{"facts":[{"statement":"s","source_aliases":["s1"],"confidence":3}]}""")]        // not a string
    [InlineData("""{"facts":[{"source_aliases":["s1"],"confidence":"high"}]}""")]                   // statement missing
    public void FactsV1_OutputThatBreaksTheStrictSchema_ThrowsJsonException(string json)
    {
        // The adapter maps a JsonException to InvalidOutput (D7), so a malformed output is a retryable outcome,
        // never a silently wrong fact.
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<FactsV1>(json, Options));
    }

    [Fact]
    public void FactsOptions_Defaults_FollowThePlan()
    {
        var options = new FactsOptions();

        options.MaxSources.ShouldBe(10);
        options.PerSourceCharCap.ShouldBe(20_000);
        options.MaxInputTokens.ShouldBe(60_000);
        options.MaxFacts.ShouldBe(40);
        options.ToLimits().ShouldBe(new FactInputLimits(10, 20_000, 60_000));
        new FactsOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(nameof(FactsOptions.MaxSources), 0)]
    [InlineData(nameof(FactsOptions.MaxSources), 11)]
    [InlineData(nameof(FactsOptions.PerSourceCharCap), 0)]
    [InlineData(nameof(FactsOptions.MaxInputTokens), 0)]
    [InlineData(nameof(FactsOptions.MaxFacts), 4)]
    [InlineData(nameof(FactsOptions.MaxFacts), 41)]
    public void FactsOptions_OutOfRangeValues_FailStartupValidation(string key, int value)
    {
        var options = new FactsOptions();
        switch (key)
        {
            case nameof(FactsOptions.MaxSources):
                options.MaxSources = value;
                break;
            case nameof(FactsOptions.PerSourceCharCap):
                options.PerSourceCharCap = value;
                break;
            case nameof(FactsOptions.MaxInputTokens):
                options.MaxInputTokens = value;
                break;
            default:
                options.MaxFacts = value;
                break;
        }

        var result = new FactsOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldHaveSingleItem().ShouldContain(key);
    }
}
