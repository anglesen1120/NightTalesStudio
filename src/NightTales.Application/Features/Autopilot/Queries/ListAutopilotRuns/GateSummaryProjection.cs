using System.Text.Json;
using NightTales.Application.Features.Autopilot.Dtos;
using NightTales.Application.Features.Autopilot.Gates;

namespace NightTales.Application.Features.Autopilot.Queries.ListAutopilotRuns;

/// <summary>
/// The read-only projection of one run's stored <c>gates.v1</c> document into the
/// <see cref="GateSummaryDto"/> the run list carries (02.8.3 plan §4 UC7).
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads the keys <see cref="GateResultWriter"/> writes</b> — <see cref="GateResultWriter.SchemaVersionKeyName"/>,
/// <see cref="GateResultWriter.ModeKeyName"/>, <see cref="GateResultWriter.StoryKeyName"/> and
/// <see cref="GateResultWriter.VideoKeyName"/>, each block's <c>pass</c> and <c>failures</c> — and nothing else: the
/// evidence, the profile name and the ids stay in the document.
/// </para>
/// <para>
/// <b>A row with no usable document is <see langword="null"/>, never an error</b> (task §Failure handling: "gate_results
/// absent on an old run → <c>Gates = null</c>"). A half-written or unparseable value degrades the same way, which is
/// the rule <see cref="GateResultWriter"/> itself uses when it merges ("degrades to re-evaluate, never to an
/// unreadable verdict"): one unreadable column must not fail a page of runs.
/// </para>
/// <para>
/// <b>One <c>failures</c> list.</b> Each gate keeps its own <c>failures</c> array inside its block, so the summary
/// concatenates them in document order — story first — and drops duplicates; the codes are distinct per run either
/// way, but a re-evaluated gate may report one twice.
/// </para>
/// </remarks>
internal static class GateSummaryProjection
{
    /// <summary>The <c>pass</c> member of a gate block.</summary>
    private const string PassKeyName = "pass";

    /// <summary>The <c>failures</c> member of a gate block.</summary>
    private const string FailuresKeyName = "failures";

    /// <summary>
    /// Summarises a stored <c>gates.v1</c> document.
    /// </summary>
    /// <param name="document">The stored document, in whatever state the row carries it.</param>
    /// <returns>The summary, or <see langword="null"/> when there is no document to read.</returns>
    internal static GateSummaryDto? From(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return null;
        }

        try
        {
            using var parsed = JsonDocument.Parse(document);
            var root = parsed.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new GateSummaryDto(
                Text(root, GateResultWriter.ModeKeyName),
                Pass(root, GateResultWriter.StoryKeyName),
                Pass(root, GateResultWriter.VideoKeyName),
                Failures(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One string member of an object, or <see langword="null"/> when it is absent or not a string.</summary>
    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>One gate block's <c>pass</c>, or <see langword="null"/> when the block has not been written yet.</summary>
    private static bool? Pass(JsonElement root, string blockName)
    {
        if (!root.TryGetProperty(blockName, out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!block.TryGetProperty(PassKeyName, out var pass))
        {
            return null;
        }

        return pass.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>Both blocks' <c>failures</c>, story first, without duplicates.</summary>
    private static List<string> Failures(JsonElement root)
    {
        string[] blocks = [GateResultWriter.StoryKeyName, GateResultWriter.VideoKeyName];
        var failures = new List<string>();

        foreach (var blockName in blocks)
        {
            if (!root.TryGetProperty(blockName, out var block)
                || block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty(FailuresKeyName, out var stored)
                || stored.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var failure in stored.EnumerateArray())
            {
                if (failure.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var code = failure.GetString();
                if (!string.IsNullOrEmpty(code) && !failures.Contains(code, StringComparer.Ordinal))
                {
                    failures.Add(code);
                }
            }
        }

        return failures;
    }
}
