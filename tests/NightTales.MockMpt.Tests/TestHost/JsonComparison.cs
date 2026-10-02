using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.RenderNode.Contract;

namespace NightTales.MockMpt.Tests.TestHost;

/// <summary>
/// JSON equality for the D14 fixtures (D18: "JSON equality ignoring timestamps"): member names are compared
/// ordinally but not in order, numbers by value (<c>70</c> = <c>70.0</c>) and strings that are both ISO-8601
/// instants by instant. The members the contract generates for a replaying task — <c>task_id</c>,
/// <c>render_job_id</c> and every timestamp — are skipped at any depth, which is exactly the per-fixture ignore
/// list 11.1.2-T1's Design names. The failure message names the path that differs.
/// </summary>
internal static class JsonComparison
{
    private static readonly HashSet<string> IgnoredMembers = new(StringComparer.Ordinal)
    {
        "task_id",
        "render_job_id",
        "submitted_at",
        "started_at",
        "finished_at",
        "checked_at",
        "sampled_at",
    };

    internal static string? Difference(string expectedJson, string actualJson)
    {
        var expected = JsonNode.Parse(expectedJson, new JsonNodeOptions { PropertyNameCaseInsensitive = false });
        var actual = JsonNode.Parse(actualJson, new JsonNodeOptions { PropertyNameCaseInsensitive = false });
        return Compare(expected, actual, "$");
    }

    private static string? Compare(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null
                ? null
                : $"{path}: expected {(expected is null ? "null" : Describe(expected))}, actual {(actual is null ? "null" : Describe(actual))}";
        }

        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            return CompareObjects(expectedObject, actualObject, path);
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count)
            {
                return $"{path}: expected {expectedArray.Count} elements, actual {actualArray.Count}";
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

        if (expected is JsonObject || expected is JsonArray || actual is JsonObject || actual is JsonArray)
        {
            return $"{path}: expected {Describe(expected)}, actual {Describe(actual)}";
        }

        return ScalarsEqual(expected, actual, out var detail) ? null : $"{path}: {detail}";
    }

    private static string? CompareObjects(JsonObject expected, JsonObject actual, string path)
    {
        foreach (var (name, expectedValue) in expected)
        {
            if (IgnoredMembers.Contains(name))
            {
                continue;
            }

            if (!actual.TryGetPropertyValue(name, out var actualValue))
            {
                return $"{path}.{name}: missing in the actual JSON";
            }

            var difference = Compare(expectedValue, actualValue, $"{path}.{name}");
            if (difference is not null)
            {
                return difference;
            }
        }

        foreach (var (name, _) in actual)
        {
            if (!IgnoredMembers.Contains(name) && !expected.ContainsKey(name))
            {
                return $"{path}.{name}: unexpected in the actual JSON";
            }
        }

        return null;
    }

    private static bool ScalarsEqual(JsonNode expected, JsonNode actual, out string detail)
    {
        var expectedKind = expected.GetValueKind();
        var actualKind = actual.GetValueKind();
        if (expectedKind != actualKind)
        {
            detail = $"expected {Describe(expected)}, actual {Describe(actual)}";
            return false;
        }

        if (string.Equals(expected.ToJsonString(), actual.ToJsonString(), StringComparison.Ordinal))
        {
            detail = string.Empty;
            return true;
        }

        if (expectedKind == JsonValueKind.Number
            && expected.AsValue().TryGetValue<decimal>(out var expectedNumber)
            && actual.AsValue().TryGetValue<decimal>(out var actualNumber))
        {
            detail = $"expected {expectedNumber}, actual {actualNumber}";
            return expectedNumber == actualNumber;
        }

        if (expectedKind == JsonValueKind.String
            && expected.AsValue().TryGetValue<string>(out var expectedText)
            && actual.AsValue().TryGetValue<string>(out var actualText)
            && UtcDateTimeOffsetJsonConverter.TryParse(expectedText, out var expectedInstant)
            && UtcDateTimeOffsetJsonConverter.TryParse(actualText, out var actualInstant))
        {
            detail = $"expected {expectedText} ({expectedInstant:O}), actual {actualText} ({actualInstant:O})";
            return expectedInstant.UtcDateTime == actualInstant.UtcDateTime;
        }

        detail = $"expected {Describe(expected)}, actual {Describe(actual)}";
        return false;
    }

    private static string Describe(JsonNode node) =>
        node.GetValueKind() switch
        {
            JsonValueKind.String => string.Create(CultureInfo.InvariantCulture, $"\"{node.GetValue<string>()}\""),
            _ => node.ToJsonString(),
        };
}
