using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Assets;
using NightTales.Domain.Rendering;
using NightTales.Domain.Series;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// <see cref="VideoGateInputFactory"/> (task 02.8.2-T1, W2-3): the stored document is read defensively, so every
/// member the report omits, mistypes or renames becomes a null gate fact — which fails that check with
/// <c>field_absent</c> — instead of being read as a zero or a <c>false</c>.
/// </summary>
/// <remarks>
/// The oracle is <see cref="RenderQualityReportV1"/> itself: the mapping test serialises the type the ingest writes
/// (05 README D10) and feeds those very bytes back, so a member renamed on either side fails here rather than
/// silently degrading to "owner review" in production. No database and no host are involved.
/// </remarks>
public sealed class VideoGateInputFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The lowercase SHA-256 hex a stored report and an asset row both carry.</summary>
    private static readonly string Sha256 = new('a', 64);

    private static readonly VideoGateInputFactory Factory = new(NullLogger<VideoGateInputFactory>.Instance);

    [Fact]
    public void Build_SerializedQualityReport_MapsEveryGateFact()
    {
        var job = FinishedJob(Params(subtitlesEnabled: true), JsonSerializer.Serialize(Report()));
        var asset = StoredAsset();

        var input = Factory.Build(job, asset, Aspect.Portrait, 2);

        var facts = input.Report.ShouldNotBeNull();
        facts.Pass.ShouldBe(true);
        facts.DurationSec.ShouldBe(120.5);
        facts.TtsDurationSec.ShouldBe(120.0);
        facts.IntegratedLufs.ShouldBe(-15.2);
        facts.Width.ShouldBe(1920);
        facts.Height.ShouldBe(1080);
        facts.SubtitlesPresent.ShouldBe(true);
        facts.Sha256.ShouldBe(Sha256);
        facts.SkippedSceneCount.ShouldBe(2);
        facts.ModeratedImageCount.ShouldBe(1);
        facts.Hints.ShouldBe(["subtitle_sync_suspect", "music_none"]);

        input.RenderJob.Id.ShouldBe(job.Id);
        input.Asset.Id.ShouldBe(asset.Id);
        input.Asset.Sha256.ShouldBe(Sha256);
        input.Asset.DurationSec.ShouldBe(120.5m);
        input.SubtitlesEnabled.ShouldBeTrue();
        input.Aspect.ShouldBe(Aspect.Portrait);
        input.ImageModerationEvents.ShouldBe(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseQualityReport_NoReport_IsNull(string? stored)
    {
        Factory.ParseQualityReport(stored).ShouldBeNull();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"render-quality.v1\"")]
    [InlineData("42")]
    [InlineData("{\"schema\":\"images.v1\",\"pass\":true}")]
    public void ParseQualityReport_UnreadableOrForeignDocument_IsNull(string stored)
    {
        Factory.ParseQualityReport(stored).ShouldBeNull();
    }

    [Fact]
    public void ParseQualityReport_MissingMembers_AreNull()
    {
        var facts = Factory
            .ParseQualityReport("{\"schema\":\"render-quality.v1\",\"pass\":true,\"hints\":[\"music_none\"]}")
            .ShouldNotBeNull();

        facts.Pass.ShouldBe(true);
        facts.DurationSec.ShouldBeNull();
        facts.TtsDurationSec.ShouldBeNull();
        facts.IntegratedLufs.ShouldBeNull();
        facts.Width.ShouldBeNull();
        facts.Height.ShouldBeNull();
        facts.SubtitlesPresent.ShouldBeNull();
        facts.Sha256.ShouldBeNull();
        facts.SkippedSceneCount.ShouldBeNull();
        facts.ModeratedImageCount.ShouldBeNull();
        facts.Hints.ShouldBe(["music_none"]);
    }

    [Fact]
    public void ParseQualityReport_WrongTypedMembers_AreNull()
    {
        var facts = Factory
            .ParseQualityReport(
                """
                {
                  "schema": "render-quality.v1",
                  "pass": "yes",
                  "duration_sec": "120.5",
                  "tts_duration_sec": "120",
                  "integrated_lufs": "-15.2",
                  "width": "1920",
                  "height": "1080",
                  "subtitles_present": "true",
                  "sha256": 7
                }
                """)
            .ShouldNotBeNull();

        facts.Pass.ShouldBeNull();
        facts.DurationSec.ShouldBeNull();
        facts.TtsDurationSec.ShouldBeNull();
        facts.IntegratedLufs.ShouldBeNull();
        facts.Width.ShouldBeNull();
        facts.Height.ShouldBeNull();
        facts.SubtitlesPresent.ShouldBeNull();
        facts.Sha256.ShouldBeNull();
    }

    [Fact]
    public void ParseQualityReport_ImagesBlock_CountsSkippedAndModerated()
    {
        var facts = Factory
            .ParseQualityReport(
                """
                {
                  "schema": "render-quality.v1",
                  "pass": true,
                  "images": {
                    "schema": "images.v1",
                    "needed": 30,
                    "succeeded": 28,
                    "trimmed_count": 0,
                    "moderated_count": 1,
                    "skipped": [
                      { "slot": 7, "segment": "p:4", "reason": "moderated" },
                      { "slot": 9, "segment": "p:5", "reason": "rate_limited" }
                    ],
                    "hints": ["images_skipped"]
                  }
                }
                """)
            .ShouldNotBeNull();

        facts.SkippedSceneCount.ShouldBe(2);
        facts.ModeratedImageCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("{\"images\":{\"skipped\":[{\"slot\":1}],\"moderated_count\":\"1\"}}")]
    [InlineData("{\"images\":{\"skipped\":{\"slot\":1},\"moderated_count\":1}}")]
    [InlineData("{\"images\":{\"skipped\":[{\"slot\":1}]}}")]
    [InlineData("{\"images\":{\"moderated_count\":1}}")]
    [InlineData("{\"images\":{}}")]
    [InlineData("{}")]
    public void ParseQualityReport_ImagesBlockUnreadable_LeavesBothCountsNull(string document)
    {
        var facts = Factory.ParseQualityReport(WithSchemaAndPass(document)).ShouldNotBeNull();

        facts.SkippedSceneCount.ShouldBeNull();
        facts.ModeratedImageCount.ShouldBeNull();
    }

    [Fact]
    public void ParseQualityReport_Hints_KeepsOnlyNonEmptyStrings()
    {
        var facts = Factory
            .ParseQualityReport(
                """
                {
                  "schema": "render-quality.v1",
                  "pass": true,
                  "hints": ["subtitle_sync_suspect", 7, "", null, "music_none"]
                }
                """)
            .ShouldNotBeNull();

        facts.Hints.ShouldBe(["subtitle_sync_suspect", "music_none"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"hints\":\"subtitle_sync_suspect\"}")]
    [InlineData("{\"hints\":{\"a\":1}}")]
    public void ParseQualityReport_HintsNotAnArray_IsEmpty(string document)
    {
        var facts = Factory.ParseQualityReport(WithSchemaAndPass(document)).ShouldNotBeNull();

        facts.Hints.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ResolveSubtitlesEnabled_AbsentKeyCountsAsEnabled(bool? stored, bool expected)
    {
        VideoGateInputFactory.ResolveSubtitlesEnabled(stored).ShouldBe(expected);
    }

    [Fact]
    public void Build_SubtitlesDisabled_CarriesTheStoredFlag()
    {
        var input = Factory.Build(
            FinishedJob(Params(subtitlesEnabled: false), JsonSerializer.Serialize(Report())),
            StoredAsset(),
            Aspect.Landscape,
            0);

        input.SubtitlesEnabled.ShouldBeFalse();
        input.Aspect.ShouldBe(Aspect.Landscape);
        input.ImageModerationEvents.ShouldBe(0);
    }

    [Fact]
    public void Build_FinishedJobWithNoStoredReport_ReadsAsAbsent()
    {
        var input = Factory.Build(FinishedJob(JobParams(), qualityReportJson: null), StoredAsset(), Aspect.Landscape, 0);

        input.Report.ShouldBeNull();
    }

    [Fact]
    public void Build_MissingAssetRow_IsTheFailClosedPlaceholder()
    {
        var input = Factory.Build(
            FinishedJob(JobParams(), "{\"schema\":\"render-quality.v1\"}"),
            asset: null,
            Aspect.Landscape,
            0);

        input.Asset.Id.ShouldBe(Guid.Empty);
        input.Asset.Sha256.ShouldBeNull();
        input.Asset.DurationSec.ShouldBeNull();
    }

    [Fact]
    public void AssetFactsOf_MissingRow_IsTheFailClosedPlaceholder()
    {
        var facts = VideoGateInputFactory.AssetFactsOf(null);

        facts.Id.ShouldBe(Guid.Empty);
        facts.Sha256.ShouldBeNull();
        facts.DurationSec.ShouldBeNull();
    }

    [Fact]
    public void AssetFactsOf_StoredRow_MapsTheHashAndDuration()
    {
        var asset = StoredAsset();

        var facts = VideoGateInputFactory.AssetFactsOf(asset);

        facts.Id.ShouldBe(asset.Id);
        facts.Sha256.ShouldBe(asset.Sha256);
        facts.DurationSec.ShouldBe(asset.DurationSec);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"cadence\":{\"daily_cap\":3}}")]
    [InlineData("{\"gate_thresholds_ref\":\"   \"}")]
    [InlineData("{\"gate_thresholds_ref\":7}")]
    [InlineData("{\"gate_thresholds_ref\":null}")]
    public void ReadGateThresholdsRef_NoUsableRef_IsNull(string? settings)
    {
        Factory.ReadGateThresholdsRef(settings).ShouldBeNull();
    }

    [Theory]
    [InlineData("{\"gate_thresholds_ref\":\"strict\"}", "strict")]
    [InlineData("{\"daily_cap\":3,\"gate_thresholds_ref\":\"default\",\"tone\":\"calm\"}", "default")]
    public void ReadGateThresholdsRef_ConfiguredRef_IsRead(string settings, string expected)
    {
        Factory.ReadGateThresholdsRef(settings).ShouldBe(expected);
    }

    /// <summary>
    /// SEC-6: the schema of a foreign report is stored text, so the log property carries a cleaned, bounded excerpt
    /// — at most 40 characters, with no control character and no line break — instead of the raw value.
    /// </summary>
    [Theory]
    [InlineData("\0\a\t\r\n\u007f\u0085")]
    [InlineData("\u0001\t\r\n\u009f")]
    public void ParseQualityReport_ForeignSchema_LogsABoundedCleanedExcerpt(string controls)
    {
        var logger = new RecordingLogger();
        var factory = new VideoGateInputFactory(logger);
        var document = new JsonObject { ["schema"] = controls + new string('x', 200) }.ToJsonString();

        factory.ParseQualityReport(document).ShouldBeNull();

        var logged = logger.Values["Schema"].ShouldBeOfType<string>();
        logged.Length.ShouldBe(40, "the stored schema is cut to the log bound");
        logged.ShouldBe(
            new string(' ', controls.Length) + new string('x', 40 - controls.Length),
            "every control character became a space, line feed and carriage return too");
        logged.Any(char.IsControl).ShouldBeFalse();
    }

    /// <summary>
    /// SEC-6: the excerpt is cut at the bound without leaving half a surrogate pair in the log property.
    /// </summary>
    [Fact]
    public void ParseQualityReport_ForeignSchema_NeverSplitsASurrogatePair()
    {
        var logger = new RecordingLogger();
        var factory = new VideoGateInputFactory(logger);
        var document = new JsonObject { ["schema"] = new string('x', 39) + "\U0001F600" + new string('x', 10) }
            .ToJsonString();

        factory.ParseQualityReport(document).ShouldBeNull();

        var logged = logger.Values["Schema"].ShouldBeOfType<string>();
        logged.ShouldBe(new string('x', 39), "the half pair at the bound is dropped, not kept as a lone surrogate");
    }

    /// <summary>
    /// SEC-3: a stored number beyond any real measurement is read as the double the JSON carries; the Domain rules
    /// turn it into a failed check (<c>field_absent</c>) instead of an <see cref="OverflowException"/>. This test
    /// pins the boundary: the factory parses, the rule decides.
    /// </summary>
    [Fact]
    public void ParseQualityReport_EnormousNumbers_ArePassedToTheRulesUnchanged()
    {
        var facts = Factory
            .ParseQualityReport(
                "{\"schema\":\"render-quality.v1\",\"pass\":true,\"duration_sec\":1e300,\"integrated_lufs\":1e300}")
            .ShouldNotBeNull();

        facts.DurationSec.ShouldBe(1e300);
        facts.IntegratedLufs.ShouldBe(1e300);
    }

    /// <summary>The stored <c>render-quality.v1</c> document the ingest writes, members and all (05 D10).</summary>
    private static RenderQualityReportV1 Report() => new()
    {
        Pass = true,
        PolicyVersion = 1,
        CheckToolVersion = "render_check.py@1",
        DurationSec = 120.5,
        TtsDurationSec = 120.0,
        IntegratedLufs = -15.2,
        TruePeakDbtp = -1.1,
        Width = 1920,
        Height = 1080,
        SubtitlesPresent = true,
        Sha256 = Sha256,
        Normalized = true,
        Checks = [],
        Hints = ["subtitle_sync_suspect", "music_none"],
        Images = JsonNode.Parse(
            """
            {
              "schema": "images.v1",
              "needed": 30,
              "succeeded": 28,
              "trimmed_count": 0,
              "moderated_count": 1,
              "skipped": [
                { "slot": 7, "segment": "p:4", "reason": "moderated" },
                { "slot": 9, "segment": "p:5", "reason": "rate_limited" }
              ],
              "hints": ["images_skipped"]
            }
            """),
    };

    private static RenderParamsV1 Params(bool subtitlesEnabled) => new(
        RenderParamsV1.CurrentMapperVersion,
        Guid.CreateVersion7(),
        "portrait",
        "vi",
        "kokoro:test",
        Guid.CreateVersion7(),
        "style=test@0123abcd;neg=v1",
        new ImagePlanSummary(3, 2, 4, 2, 0, [], ["hook", "p:0"], "style=test@0123abcd;neg=v1"),
        new SubtitlesParams(subtitlesEnabled, "portrait@1"),
        null,
        3);

    private static RenderParamsV1 JobParams() => Params(subtitlesEnabled: true);

    /// <summary>A <c>succeeded</c> render job carrying <paramref name="qualityReportJson"/> when one is given.</summary>
    private static RenderJob FinishedJob(RenderParamsV1 @params, string? qualityReportJson)
    {
        var job = RenderJob.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1.25m,
            @params,
            1,
            Now).Value;

        job.MarkSubmitted("task-1", Guid.CreateVersion7(), Now).IsError.ShouldBeFalse();
        job.MarkIngesting(Now).IsError.ShouldBeFalse();
        if (qualityReportJson is not null)
        {
            job.RecordQualityReport(qualityReportJson).IsError.ShouldBeFalse();
        }

        job.Succeed(Now).IsError.ShouldBeFalse();

        return job;
    }

    /// <summary>A stored video asset row of the same document's hash.</summary>
    private static Asset StoredAsset() => Asset.Create(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        AssetKind.Video,
        $"media/{Guid.CreateVersion7():N}.mp4",
        "video/mp4",
        1_024L,
        120.5m,
        Sha256,
        Now.AddDays(7),
        Now).Value;

    /// <summary>The shape every unreadable-images case shares: a well-formed report whose members are the test's.</summary>
    private static string WithSchemaAndPass(string document)
    {
        var root = JsonNode.Parse(document)!.AsObject();
        root["schema"] = "render-quality.v1";
        root["pass"] = true;

        return root.ToJsonString();
    }

    /// <summary>Captures the named property of every log call, keyed by its property name (SEC-6).</summary>
    private sealed class RecordingLogger : ILogger<VideoGateInputFactory>
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values)
            {
                return;
            }

            foreach (var (key, value) in values)
            {
                Values[key] = value;
            }
        }
    }
}
