using System.Text.Json;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering.Quality;
using NightTales.Domain.Series;
using NightTales.RenderNode.Contract;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// The D14 render contract fixtures (<c>worker-node/contract/fixtures/render-{check,quality}.v1.*.json</c>)
/// and the small helpers the verdict tests share. The fixtures are the contract artefacts: the adapter
/// serves them (11.3.5-T1), mock-mpt replays them (11.1.2-T1) and these tests read the same bytes through
/// the same <see cref="RenderNodeJson.Strict"/> reader, so a renamed or re-encoded member fails here
/// first.
/// </summary>
internal static class RenderFixtures
{
    /// <summary>The fixture directory as copied next to the test assembly.</summary>
    internal static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures");

    internal static TheoryData<string> CheckTheoryData() => TheoryDataOf("render-check.v1.*.json");

    internal static TheoryData<string> ReportTheoryData() => TheoryDataOf("render-quality.v1.*.json");

    internal static RenderCheckV1 ReadCheck(string file) => Read<RenderCheckV1>(file);

    internal static RenderQualityReportV1 ReadReport(string file) => Read<RenderQualityReportV1>(file);

    internal static string ReadJson(string file) => File.ReadAllText(Path.Combine(Directory, file));

    /// <summary>The report of evaluating one check with every expectation met unless a test says otherwise.</summary>
    internal static RenderQualityReportV1 Evaluate(
        RenderCheckV1? check,
        bool subtitlesEnabled = true,
        bool musicSelected = true,
        Aspect aspect = Aspect.Landscape,
        RenderQualityPolicy? policy = null) =>
        RenderQualityEvaluator.Evaluate(
            check,
            new RenderExpectations(aspect, subtitlesEnabled, musicSelected),
            policy ?? new RenderQualityPolicy());

    /// <summary>The check with the given id, so a test names the rule it asserts.</summary>
    internal static QualityCheck CheckOf(RenderQualityReportV1 report, string id) =>
        report.Checks.Single(check => check.Id == id);

    /// <summary>
    /// A syntactically complete report with the <c>render-check.v1.ok</c> numbers, so a boundary test
    /// changes exactly one measure. A <c>null</c> <paramref name="lufs"/>/<paramref name="truePeak"/>
    /// produces the unmeasurable shape of <c>render-check.v1.silent</c> (measures <c>null</c>, never
    /// <c>-Infinity</c>).
    /// </summary>
    internal static RenderCheckV1 SyntheticCheck(
        string[]? errors = null,
        string[]? warnings = null,
        double? durationS = 302.1,
        double? narrationS = 301.37,
        double? lufs = -15,
        double? truePeak = -1.6,
        double? longestSilenceS = 1.2,
        string? videoCodec = "h264",
        string? audioCodec = "aac",
        int? width = 1920,
        int? height = 1080,
        bool? subtitlesPresent = true,
        int cues = 212,
        int zeroDuration = 0,
        double coverageShare = 0.86,
        int longCues = 3,
        int syncSuspect = 2,
        bool? textMatchesScript = true) =>
        new RenderCheckV1(
            Schema: "render-check.v1",
            ToolVersion: "1.0.0",
            FfmpegVersion: "9.0.2",
            Input: new RenderCheckInputV1(File: "final-1.mp4", Bytes: 98123456, Sha256: "in"),
            Output: new RenderCheckOutputV1(File: "final-1.norm.mp4", Bytes: 98100000, Sha256: "out", Normalized: true),
            Container: durationS is null
                ? null
                : new RenderCheckContainerV1(Format: "mp4", DurationS: durationS.Value, Faststart: true),
            Video: width is null
                ? null
                : new RenderCheckVideoV1(
                    Codec: videoCodec ?? string.Empty,
                    PixFmt: "yuv420p",
                    Width: width.Value,
                    Height: height ?? 0,
                    Fps: 30),
            Audio: audioCodec is null
                ? null
                : new RenderCheckAudioV1(Codec: audioCodec, SampleRate: 48000, Channels: 1),
            Narration: narrationS is null ? null : new RenderCheckNarrationV1(DurationS: narrationS.Value),
            Loudness: lufs is null && truePeak is null
                ? new RenderCheckLoudnessV1(Before: null, After: null, NormalizationType: null)
                : new RenderCheckLoudnessV1(
                    Before: new RenderCheckLoudnessMeasureV1(IntegratedLufs: lufs, TruePeakDbtp: truePeak, Lra: 6),
                    After: new RenderCheckLoudnessMeasureV1(IntegratedLufs: lufs, TruePeakDbtp: truePeak, Lra: 6),
                    NormalizationType: "linear"),
            Silence: longestSilenceS is null
                ? null
                : new RenderCheckSilenceV1(LongestS: longestSilenceS.Value, TotalS: longestSilenceS.Value),
            Subtitles: subtitlesPresent is null
                ? null
                : new RenderCheckSubtitlesV1(
                    Present: subtitlesPresent.Value,
                    Cues: cues,
                    ZeroDuration: zeroDuration,
                    Overlapping: 0,
                    NonMonotonic: 0,
                    OutOfRange: 0,
                    CoverageShare: coverageShare,
                    MaxCueChars: 78,
                    LongCues: longCues,
                    CpsP95: 17.2,
                    SyncSuspect: syncSuspect,
                    TextMatchesScript: textMatchesScript,
                    TextMismatchCues: 0),
            Warnings: warnings ?? [],
            Errors: errors ?? []);

    /// <summary>
    /// Whether two JSON documents are equal, comparing numbers by value: a round-trip may canonicalise
    /// <c>30.0</c> to <c>30</c> (and a report's <c>1.0</c> to <c>1</c>), which is not a contract change.
    /// </summary>
    internal static bool DocumentsAreEqual(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);

        return ElementsAreEqual(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static TheoryData<string> TheoryDataOf(string pattern)
    {
        var data = new TheoryData<string>();
        foreach (var file in System.IO.Directory.GetFiles(Directory, pattern).Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            data.Add(file!);
        }

        return data;
    }

    private static T Read<T>(string file) =>
        JsonSerializer.Deserialize<T>(ReadJson(file), RenderNodeJson.Strict)
        ?? throw new InvalidOperationException($"'{file}' deserialised to null");

    private static bool ElementsAreEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var rightProperties = right.EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                return left.EnumerateObject().Count() == rightProperties.Count
                    && left.EnumerateObject().All(property =>
                        rightProperties.TryGetValue(property.Name, out var other) && ElementsAreEqual(property.Value, other));
            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length
                    && leftItems.Zip(rightItems).All(pair => ElementsAreEqual(pair.First, pair.Second));
            case JsonValueKind.Number:
                return left.TryGetDouble(out var leftNumber)
                    && right.TryGetDouble(out var rightNumber)
                    && leftNumber.Equals(rightNumber);
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            default:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
        }
    }
}
