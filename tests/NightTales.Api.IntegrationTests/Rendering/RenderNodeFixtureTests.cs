using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.Application.Abstractions.Rendering;
using NightTales.RenderNode.Contract;
using NightTales.TestKit.Rendering;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The contract gate of 09.6.2-T1 over the frozen fixtures of 05.4.1-T2: every body the adapter can send is
/// read by the C# contract with <c>JsonUnmappedMemberHandling.Disallow</c> — an adapter field the contract
/// lacks fails the test with the fixture path and the JSON pointer — and every body survives a round trip, so
/// a silently dropped or renamed member cannot pass. The fixtures are read, never edited.
/// </summary>
public sealed class RenderNodeFixtureTests
{
    [Fact]
    public void FixtureRoundTrip_IsLossless_WithDisallow()
    {
        ContractFixtures.Names.ShouldNotBeEmpty();

        foreach (var name in ContractFixtures.Names)
        {
            var json = ContractFixtures.Read(name);
            var roundTripped = RoundTrip(name, json);

            Difference(JsonNode.Parse(json), JsonNode.Parse(roundTripped), "$")
                .ShouldBeNull($"{name}: the contract's own writer must reproduce the fixture it reads");
        }
    }

    [Fact]
    public void MissingContractField_FailsWithPointer()
    {
        const string name = "render-status.v1.queued.json";
        var patched = JsonNode.Parse(ContractFixtures.Read(name))!.AsObject();
        patched["extra_field"] = 1;

        var failure = Should.Throw<InvalidOperationException>(
            () => DeserializeStrict<RenderStatusV1>(name, patched.ToJsonString()));

        // The failure has to name the fixture and the exact member, or a drifted adapter is undiagnosable.
        failure.Message.ShouldContain(name);
        failure.Message.ShouldContain("extra_field");
    }

    /// <summary>Reads one fixture with the strict profile and writes it back with the same profile.</summary>
    /// <param name="name">The fixture's file name, which picks the contract type.</param>
    /// <param name="json">The fixture's text.</param>
    /// <returns>The re-serialised document.</returns>
    private static string RoundTrip(string name, string json) => name switch
    {
        _ when name.StartsWith("render-submit.v1.", StringComparison.Ordinal) =>
            Serialize(DeserializeStrict<AdapterSubmitRequestV1>(name, json)),
        _ when name.StartsWith("render-status.v1.", StringComparison.Ordinal) =>
            Serialize(DeserializeStrict<RenderStatusV1>(name, json)),
        _ when name.StartsWith("render-check.v1.", StringComparison.Ordinal) =>
            Serialize(DeserializeStrict<RenderCheckV1>(name, json)),
        _ when name.StartsWith("render-node-health.v1.", StringComparison.Ordinal) =>
            Serialize(DeserializeStrict<RenderNodeHealthV1>(name, json)),
        _ when name.StartsWith("render-quality.v1.", StringComparison.Ordinal) =>
            Serialize(DeserializeStrict<RenderQualityReportV1>(name, json)),
        _ => throw new InvalidOperationException(
            $"'{name}' maps to no contract type; every fixture must map to the type the adapter sends."),
    };

    /// <summary>Serialises with the profile the fixtures are written in.</summary>
    /// <typeparam name="T">The contract type.</typeparam>
    /// <param name="value">The value to write.</param>
    /// <returns>The JSON text.</returns>
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, RenderNodeJson.Strict);

    /// <summary>
    /// Reads one document with the strict profile (<c>JsonUnmappedMemberHandling.Disallow</c>, duplicate
    /// members rejected) and reports a drift as the fixture path plus the JSON pointer of the offending
    /// member, so a failure names what a newer adapter sends.
    /// </summary>
    /// <typeparam name="T">The contract type the fixture must be.</typeparam>
    /// <param name="name">The fixture's file name.</param>
    /// <param name="json">The document's text.</param>
    /// <returns>The deserialised document.</returns>
    private static T DeserializeStrict<T>(string name, string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, RenderNodeJson.Strict)
                ?? throw new InvalidOperationException($"{name}: the document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"{name}: {exception.Path ?? "$"}: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// The difference between the fixture and its round trip, or <c>null</c> when they are the same document:
    /// member names ordinally but out of order, arrays in order, numbers by value (the contract's own writer
    /// emits a <c>double</c> in its shortest round-trippable form, so <c>-14.0</c> legitimately comes back as
    /// <c>-14</c>) and two ISO-8601 strings by instant. The same comparison the D14 replay uses.
    /// </summary>
    /// <param name="expected">The fixture's tree.</param>
    /// <param name="actual">The round trip's tree.</param>
    /// <param name="path">The JSON pointer of the value being compared.</param>
    /// <returns>The first difference, or <c>null</c>.</returns>
    private static string? Difference(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"{path}: one side is null";
        }

        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            foreach (var (name, value) in expectedObject)
            {
                if (!actualObject.TryGetPropertyValue(name, out var mine))
                {
                    return $"{path}.{name}: missing after the round trip";
                }

                if (Difference(value, mine, $"{path}.{name}") is { } difference)
                {
                    return difference;
                }
            }

            foreach (var (name, _) in actualObject.Where(entry => !expectedObject.ContainsKey(entry.Key)))
            {
                return $"{path}.{name}: added by the round trip";
            }

            return null;
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count)
            {
                return $"{path}: {expectedArray.Count} elements became {actualArray.Count}";
            }

            for (var index = 0; index < expectedArray.Count; index++)
            {
                if (Difference(expectedArray[index], actualArray[index], $"{path}[{index}]") is { } difference)
                {
                    return difference;
                }
            }

            return null;
        }

        if (expected.GetValueKind() != actual.GetValueKind())
        {
            return $"{path}: {expected.GetValueKind()} became {actual.GetValueKind()}";
        }

        if (string.Equals(expected.ToJsonString(), actual.ToJsonString(), StringComparison.Ordinal))
        {
            return null;
        }

        if (expected.GetValueKind() == JsonValueKind.Number
            && expected.AsValue().TryGetValue<decimal>(out var left)
            && actual.AsValue().TryGetValue<decimal>(out var right))
        {
            return left == right ? null : $"{path}: {left} became {right}";
        }

        if (expected.GetValueKind() == JsonValueKind.String
            && expected.AsValue().TryGetValue<string>(out var expectedText)
            && actual.AsValue().TryGetValue<string>(out var actualText)
            && UtcDateTimeOffsetJsonConverter.TryParse(expectedText, out var expectedInstant)
            && UtcDateTimeOffsetJsonConverter.TryParse(actualText, out var actualInstant)
            && expectedInstant.UtcDateTime == actualInstant.UtcDateTime)
        {
            return null;
        }

        return $"{path}: {expected.ToJsonString()} became {actual.ToJsonString()}";
    }
}
