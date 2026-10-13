using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NightTales.Domain.Stories.Prompts;
using NightTales.Infrastructure.Prompts;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Prompts;

/// <summary>
/// 02.5.3-T1: the shipped <c>facts/v1.md</c> prompt and <c>schemas/facts.v1.json</c>, their manifest
/// binding (sha256 + placeholders, 02.2.3-T1 catalog) and the wire contract the schema encodes.
/// </summary>
/// <remarks>
/// <para>
/// The startup-gate cases run over <see cref="PromptsTree"/> the same way <c>PromptCatalogTests</c> does:
/// the real <c>prompts/</c> tree read from disk with one mutation, so a failure names the rule that broke.
/// </para>
/// <para>
/// There is no JSON-schema validator package in the repository and this task adds none, so the document
/// cases evaluate the shipped schema with a small in-test checker that supports exactly the keywords
/// this schema uses (<c>type</c>, <c>required</c>, <c>additionalProperties</c>, <c>properties</c>,
/// <c>items</c>, <c>minItems</c>, <c>maxItems</c>, <c>enum</c>, <c>pattern</c>). A structural case pins the
/// same contract directly on the schema document, so a schema regression still fails even where the
/// document cases would pass vacuously.
/// </para>
/// </remarks>
public sealed class FactsPromptCatalogTests
{
    private const string PromptKey = "facts/v1.md";
    private const string SchemaKey = "schemas/facts.v1.json";
    private const string SchemaName = "facts.v1.json";

    // ---- manifest binding of the facts prompt and schema ----

    [Theory]
    [InlineData(PromptKey)]
    [InlineData(SchemaKey)]
    public void FactsManifest_ListsTheFileWithItsRealSha256(string key)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PromptsTree.Root, "manifest.json")));
        var entry = manifest.RootElement.GetProperty("files").GetProperty(key);

        entry.GetProperty("version").GetString().ShouldBe("v1");
        entry.GetProperty("sha256").GetString().ShouldBe(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(PromptsTree.Root, key)))));
    }

    [Fact]
    public void PromptCatalog_ValidatesFactsV1_Sha256AndPlaceholders()
    {
        var catalog = PromptsTree.Load(PromptsTree.Read());

        catalog.Files.ShouldContainKey(PromptKey);
        catalog.Files.ShouldContainKey(SchemaKey);
        catalog.Files[PromptKey].Version.ShouldBe("v1");
        catalog.Files[PromptKey].Sha256.ShouldBe(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(PromptsTree.Root, PromptKey)))));

        var prompt = catalog.GetStepPrompt(PromptStep.Facts);
        prompt.ShouldNotBeNullOrWhiteSpace();
        // Rule 6 of the start-up gate: a facts prompt carries no template token at all.
        prompt.ShouldNotContain("{{");
        prompt.ShouldNotContain("}}");

        var schema = catalog.GetSchema(SchemaName);
        schema.Length.ShouldBeGreaterThan(100);
        using var parsed = JsonDocument.Parse(schema);
        parsed.RootElement.GetProperty("$id").GetString().ShouldBe("facts.v1");
    }

    [Theory]
    [InlineData(PromptKey)]
    [InlineData(SchemaKey)]
    public void FactsManifest_OneChangedByte_FailsStartUpNamingTheFile(string key)
    {
        var tree = PromptsTree.Read();
        var original = tree[key].ToArray();
        // One byte replaced in place: same length, different digest.
        tree[key][original.Length / 2] = (byte)(original[original.Length / 2] == (byte)'x' ? 'y' : 'x');
        SHA256.HashData(tree[key]).ShouldNotBe(SHA256.HashData(original));

        PromptsTree.Failure(tree).Message.ShouldContain($"sha256 mismatch for {key}");
    }

    [Theory]
    [InlineData(PromptKey)]
    [InlineData(SchemaKey)]
    public void FactsManifest_DroppingTheEntryWhileTheFileShips_FailsStartUp(string key)
    {
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestEntry(tree, key);

        PromptsTree.Failure(tree).Message.ShouldContain("not tracked by the manifest");
    }

    [Fact]
    public void FactsManifest_EmbeddedCatalogServesTheFactsPromptAndSchema()
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());

        catalog.GetStepPrompt(PromptStep.Facts).ShouldNotBeNullOrWhiteSpace();
        catalog.GetSchema(SchemaName).Length.ShouldBeGreaterThan(100);
    }

    /// <summary>The prompt's section order and the strings its own rules and schema notes depend on.</summary>
    [Fact]
    public void FactsPrompt_CarriesTheRoleRulesDataRuleAndSchemaNotes()
    {
        var prompt = PromptsTree.Load(PromptsTree.Read()).GetStepPrompt(PromptStep.Facts);

        prompt.ShouldContain("Role");
        prompt.ShouldContain("Rules");
        prompt.ShouldContain("Data rule");
        prompt.ShouldContain("facts.v1");
        foreach (var token in new[] { "statement", "source_aliases", "confidence", "high", "medium", "low", "material" })
        {
            prompt.ShouldContain(token);
        }
    }

    // ---- the schema document's strict contract ----

    [Fact]
    public void FactsV1Schema_DeclaresTheStrictContract()
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PromptsTree.Root, SchemaKey)));
        var root = schema.RootElement;

        root.GetProperty("additionalProperties").ValueKind.ShouldBe(JsonValueKind.False);
        root.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ShouldBe(["facts"]);

        var facts = root.GetProperty("properties").GetProperty("facts");
        facts.GetProperty("minItems").GetInt32().ShouldBe(0);
        facts.GetProperty("maxItems").GetInt32().ShouldBe(40);

        var item = facts.GetProperty("items");
        item.GetProperty("additionalProperties").ValueKind.ShouldBe(JsonValueKind.False);
        item.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["statement", "source_aliases", "confidence"], ignoreOrder: true);

        var itemProperties = item.GetProperty("properties");
        itemProperties.GetProperty("statement").GetProperty("type").GetString().ShouldBe("string");

        var aliases = itemProperties.GetProperty("source_aliases");
        aliases.GetProperty("minItems").GetInt32().ShouldBe(1);
        var aliasItem = aliases.GetProperty("items");
        aliasItem.GetProperty("type").GetString().ShouldBe("string");
        aliasItem.GetProperty("pattern").GetString().ShouldBe("^s([1-9]|10)$");

        itemProperties.GetProperty("confidence").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["high", "medium", "low"], ignoreOrder: true);
    }

    // ---- the schema accepts and rejects documents ----

    private const string MinimalValid =
        """{"facts":[{"statement":"The disappearance was reported on a Tuesday.","source_aliases":["s1"],"confidence":"high"}]}""";

    [Fact]
    public void FactsV1Schema_AcceptsAMinimalValidDocument() =>
        Validate(MinimalValid).ShouldBeEmpty();

    [Fact]
    public void FactsV1Schema_AcceptsAnEmptyFactsArray() =>
        Validate("""{"facts":[]}""").ShouldBeEmpty();

    [Fact]
    public void FactsV1Schema_RejectsEmptySourceAliases() =>
        Validate("""{"facts":[{"statement":"A fact.","source_aliases":[],"confidence":"high"}]}""")
            .ShouldNotBeEmpty();

    [Fact]
    public void FactsV1Schema_RejectsMissingConfidence() =>
        Validate("""{"facts":[{"statement":"A fact.","source_aliases":["s1"]}]}""")
            .ShouldNotBeEmpty();

    [Theory]
    [InlineData("s0")]
    [InlineData("s11")]
    [InlineData("S1")]
    [InlineData("source 1")]
    [InlineData("f1")]
    public void FactsV1Schema_RejectsAnAliasOutsideThePattern(string alias) =>
        Validate($$"""{"facts":[{"statement":"A fact.","source_aliases":["{{alias}}"],"confidence":"high"}]}""")
            .ShouldNotBeEmpty();

    [Theory]
    [InlineData("certain")]
    [InlineData("High")]
    [InlineData("HIGH")]
    [InlineData("")]
    public void FactsV1Schema_RejectsAConfidenceOutsideTheEnum(string confidence) =>
        Validate($$"""{"facts":[{"statement":"A fact.","source_aliases":["s1"],"confidence":"{{confidence}}"}]}""")
            .ShouldNotBeEmpty();

    [Fact]
    public void FactsV1Schema_RejectsAnExtraPropertyAtEitherLevel()
    {
        Validate("""{"facts":[],"extra":1}""").ShouldNotBeEmpty();
        Validate("""{"facts":[{"statement":"A fact.","source_aliases":["s1"],"confidence":"high","id":"f1"}]}""")
            .ShouldNotBeEmpty();
    }

    [Fact]
    public void FactsV1Schema_RejectsMissingRequiredKeys()
    {
        Validate("""{}""").ShouldNotBeEmpty();
        Validate("""{"facts":[{"source_aliases":["s1"],"confidence":"high"}]}""").ShouldNotBeEmpty();
        Validate("""{"facts":[{"statement":"A fact.","confidence":"high"}]}""").ShouldNotBeEmpty();
    }

    [Fact]
    public void FactsV1Schema_RejectsMoreThanFortyFacts()
    {
        Validate(Facts(40)).ShouldBeEmpty();
        Validate(Facts(41)).ShouldNotBeEmpty();
    }

    private static string Facts(int count) =>
        """{"facts":["""
        + string.Join(',', Enumerable.Repeat(
            """{"statement":"A fact.","source_aliases":["s1"],"confidence":"low"}""", count))
        + "]}";

    // ---- a minimal checker for the subset of JSON Schema this file uses ----

    /// <summary>Validates <paramref name="documentJson"/> against the shipped <c>facts.v1.json</c>.</summary>
    /// <param name="documentJson">The model-output document as raw JSON text.</param>
    /// <returns>One message per constraint the document violates; empty when the document is valid.</returns>
    private static List<string> Validate(string documentJson)
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PromptsTree.Root, SchemaKey)));
        using var document = JsonDocument.Parse(documentJson);

        var errors = new List<string>();
        Check(schema.RootElement, document.RootElement, "$", errors);

        return errors;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(item => item.GetString()!).ToArray()
                : new[] { type.GetString()! };

            foreach (var allowed in types)
            {
                if (!MatchesType(allowed, value))
                {
                    errors.Add($"{path}: expected {allowed}, found {value.ValueKind}");
                }
            }
        }

        if (schema.TryGetProperty("enum", out var choices)
            && !choices.EnumerateArray().Any(choice => choice.GetRawText() == value.GetRawText()))
        {
            errors.Add($"{path}: not one of the enum values");
        }

        if (value.ValueKind == JsonValueKind.String
            && schema.TryGetProperty("pattern", out var pattern)
            && !Regex.IsMatch(value.GetString()!, pattern.GetString()!, RegexOptions.CultureInvariant))
        {
            errors.Add($"{path}: does not match {pattern.GetString()}");
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var length = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var min) && length < min.GetInt32())
            {
                errors.Add($"{path}: fewer than {min.GetInt32()} items");
            }

            if (schema.TryGetProperty("maxItems", out var max) && length > max.GetInt32())
            {
                errors.Add($"{path}: more than {max.GetInt32()} items");
            }

            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var index = 0;
                foreach (var element in value.EnumerateArray())
                {
                    Check(itemSchema, element, $"{path}[{index++}]", errors);
                }
            }
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    errors.Add($"{path}: missing {name}");
                }
            }
        }

        var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;

        if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(property.Name, out _))
                {
                    errors.Add($"{path}: unexpected property {property.Name}");
                }
            }
        }

        if (properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Check(propertySchema, property.Value, $"{path}.{property.Name}", errors);
            }
        }
    }

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };
}
