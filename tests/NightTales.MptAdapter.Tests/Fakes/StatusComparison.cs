using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace NightTales.MptAdapter.Tests.Fakes;

/// <summary>
/// The D18 comparison for a <c>render-status.v1</c> body: the adapter's JSON must equal the frozen fixture
/// except for the fields a test cannot pin (timestamps, and the blocks an owner task has not wired yet —
/// <c>images</c> belongs to 03.4.3-T1/T4, <c>check</c>/<c>output</c> to 11.3.5-T3). Member names compare
/// ordinally but not in order, numbers by value, so <c>percent: 20</c> and <c>20.0</c> are the same.
/// </summary>
internal static class StatusComparison
{
    internal static void ShouldMatchFixture(string fixtureName, string actualJson, params string[] ignoredMembers)
    {
        var expected = JsonNode.Parse(FixtureFiles.Read(fixtureName), new JsonNodeOptions { PropertyNameCaseInsensitive = false });
        var actual = JsonNode.Parse(actualJson, new JsonNodeOptions { PropertyNameCaseInsensitive = false });

        foreach (var member in ignoredMembers)
        {
            (expected as JsonObject)?.Remove(member);
            (actual as JsonObject)?.Remove(member);
        }

        var difference = Compare(expected, actual, "$");
        difference.ShouldBeNull(
            $"{fixtureName}: the status differs from the frozen fixture at {difference}."
            + $"\nexpected: {expected?.ToJsonString()}\nactual:   {actual?.ToJsonString()}");
    }

    private static string? Compare(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : path;
        }

        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            foreach (var (name, value) in expectedObject)
            {
                if (!actualObject.TryGetPropertyValue(name, out var other))
                {
                    return $"{path}.{name} (missing)";
                }

                var difference = Compare(value, other, $"{path}.{name}");
                if (difference is not null)
                {
                    return difference;
                }
            }

            foreach (var name in actualObject.Select(property => property.Key))
            {
                if (!expectedObject.ContainsKey(name))
                {
                    return $"{path}.{name} (unexpected)";
                }
            }

            return null;
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count)
            {
                return $"{path} (count {expectedArray.Count} != {actualArray.Count})";
            }

            for (var index = 0; index < expectedArray.Count; index++)
            {
                var difference = Compare(expectedArray[index], actualArray[index], $"{path}[{index}]");
                if (difference is not null)
                {
                    return difference;
                }
            }

            return null;
        }

        if (expected is JsonObject or JsonArray || actual is JsonObject or JsonArray)
        {
            return path;
        }

        return ScalarsEqual(expected, actual) ? null : path;
    }

    private static bool ScalarsEqual(JsonNode expected, JsonNode actual)
    {
        var left = expected.GetValue<JsonElement>();
        var right = actual.GetValue<JsonElement>();

        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
        {
            return left.TryGetDecimal(out var leftValue) && right.TryGetDecimal(out var rightValue)
                ? leftValue == rightValue
                : string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
        }

        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
        };
    }
}
