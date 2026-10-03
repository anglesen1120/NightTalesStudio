using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightTales.MockMpt.Failures;
using NightTales.MockMpt.Fixtures;
using NightTales.MockMpt.Images;
using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// QA boundary and negative tests for failure injection (11.1.2-T2), written by the QA agent: parser edges,
/// keyword matching rules, precedence corners and end-to-end rate/keyword paths.
/// </summary>
public sealed class FailureInjectionQaTests
{
    private static FailureInjector Injector(MockMptOptions options, ILogger<FailureInjector>? logger = null) =>
        new(options, FixtureSet.Load(FixtureFiles.Root), logger ?? new ListLogger());

    private static MptSubmitFields Mpt(string? script = null, Action<JsonArray>? terms = null)
    {
        var node = JsonNode.Parse(SubmitBodies.Build(SubmitBodies.Landscape, script: script))!.AsObject();
        if (terms is not null)
        {
            terms((JsonArray)node["mpt"]!["video_terms"]!);
        }

        return JsonSerializer.Deserialize<AdapterSubmitRequestV1>(node.ToJsonString(), RenderNodeJson.Strict)!.Mpt;
    }

    private static string BodyWith(string script) => SubmitBodies.Build(SubmitBodies.Landscape, script: script);

    private static async Task<RenderStatusV1> TerminalAsync(MockMptTestHost host, string body)
    {
        var taskId = await host.SubmitAsync(body);
        return (await host.PollUntilTerminalAsync(taskId))[^1];
    }

    [Theory]
    [InlineData("TTS_FAILED")]
    [InlineData("Tts_Failed")]
    [InlineData("quality:")]
    [InlineData("quality")]
    [InlineData("none")]
    [InlineData("tts_failed,timeout")]
    public void ModeParser_Rejects(string raw)
    {
        FailModeParser.TryParse(raw, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain(raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ModeParser_BlankIsUnset(string? raw)
    {
        FailModeParser.TryParse(raw, out var mode, out var error).ShouldBeTrue();

        error.ShouldBeNull();
        mode.IsInjected.ShouldBeFalse();
    }

    [Fact]
    public void ModeParser_TrimsSurroundingWhitespace()
    {
        FailModeParser.TryParse("  node_error ", out var mode, out _).ShouldBeTrue();

        mode.Kind.ShouldBe(FailKind.NodeError);
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("1", 1.0)]
    [InlineData("0.0", 0.0)]
    [InlineData("1.0", 1.0)]
    [InlineData("1e-1", 0.1)]
    [InlineData(null, 0.0)]
    [InlineData("  ", 0.0)]
    public void Rate_BoundaryValues_Accepted(string? raw, double expected)
    {
        FailRate.TryParse(raw, out var rate, out var error).ShouldBeTrue(error);

        rate.ShouldBe(expected);
    }

    [Theory]
    [InlineData("1.0000001")]
    [InlineData("-0")]
    [InlineData("-0.0001")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("50%")]
    [InlineData("0,5")]
    public void Rate_Invalid_Rejected(string raw)
    {
        // "-0" parses as negative zero, which is within [0,1]; every other value must be rejected.
        var accepted = FailRate.TryParse(raw, out _, out var error);

        if (raw == "-0")
        {
            accepted.ShouldBeTrue();
            return;
        }

        accepted.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("MOCK_FAIL_RATE");
    }

    [Fact]
    public void Rate_DrawIsUniformInUnitInterval_AndStable()
    {
        var ids = Enumerable.Range(0, 2000).Select(i => Guid.Parse($"00000000-0000-7000-8000-{i:D12}")).ToList();

        var draws = ids.Select(FailRate.Draw).ToList();

        draws.ShouldAllBe(value => value >= 0 && value < 1);
        ids.Select(FailRate.Draw).ShouldBe(draws);
        draws.Distinct().Count().ShouldBe(draws.Count);
        draws.Average().ShouldBeInRange(0.45, 0.55);
    }

    [Fact]
    public void Rate_One_CoversEveryModeOfTheFixedSet()
    {
        var ids = Enumerable.Range(0, 400).Select(i => Guid.Parse($"00000000-0000-7000-8000-{i:D12}")).ToList();

        var chosen = ids.Select(id => FailRate.Select(1, id)).ToList();

        chosen.ShouldAllBe(mode => mode != null);
        chosen.Select(mode => mode!.Value.Kind).Distinct().Order().ShouldBe(
            FailRate.Set.Select(mode => mode.Kind).Order().ToList());
        FailRate.Set.Select(mode => mode.Kind).ShouldBe(
            [FailKind.TtsFailed, FailKind.AssemblyFailed, FailKind.NodeError, FailKind.LostTask]);
    }

    [Fact]
    public void Rate_SmallRate_FiresOnlyBelowTheRate()
    {
        var ids = Enumerable.Range(0, 1000).Select(i => Guid.Parse($"00000000-0000-7000-8000-{i:D12}")).ToList();

        foreach (var id in ids)
        {
            (FailRate.Select(0.1, id) is not null).ShouldBe(FailRate.Draw(id) < 0.1);
        }
    }

    [Fact]
    public void Rate_NeverPicksTimeoutOrQuality()
    {
        FailRate.Set.ShouldNotContain(new FailMode(FailKind.Timeout));
        FailRate.Set.ShouldNotContain(new FailMode(FailKind.ImageModerated));
        FailRate.Set.ShouldAllBe(mode => mode.Kind != FailKind.Quality);
    }

    [Theory]
    [InlineData("__FAIL_assembly_failed__")]
    [InlineData("__Fail_image__")]
    [InlineData("__fail_ASSEMBLY_FAILED__")]
    [InlineData("__fail_assembly_failed_")]
    [InlineData("_fail_assembly_failed__")]
    [InlineData("__fail__")]
    [InlineData("__fail____")]
    public void Keyword_CaseAndShapeMismatch_IsIgnored(string script)
    {
        var plan = Injector(MockMptTestHost.DefaultOptions).Resolve(Guid.CreateVersion7(), Guid.CreateVersion7(), Mpt(script));

        plan.Source.ShouldBe(FailureInjector.SourceNone);
        plan.Mode.IsInjected.ShouldBeFalse();
        plan.Image.Kind.ShouldBe(ImageReportKind.Ok);
    }

    [Theory(Skip = "QA-1: FailKeywords.ScanFail matches `__fail_<name>__` inside a longer token (spec: whole-token); enable when fixed")]
    [InlineData("pre__fail_assembly_failed__post")]
    [InlineData("xx__fail_assembly_failed__")]
    [InlineData("__fail_assembly_failed__yy")]
    [InlineData("a__fail_image__b")]
    public void Keyword_NotWholeToken_IsIgnored(string script)
    {
        var plan = Injector(MockMptTestHost.DefaultOptions).Resolve(Guid.CreateVersion7(), Guid.CreateVersion7(), Mpt(script));

        plan.Source.ShouldBe(FailureInjector.SourceNone);
        plan.Mode.IsInjected.ShouldBeFalse();
        plan.Image.Kind.ShouldBe(ImageReportKind.Ok);
    }

    [Fact]
    public void Keyword_InVideoTerms_Applies()
    {
        var plan = Injector(MockMptTestHost.DefaultOptions).Resolve(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Mpt(terms: terms => terms[2] = terms[2]!.GetValue<string>() + " __fail_node_error__"));

        plan.Source.ShouldBe(FailureInjector.SourceKeyword);
        plan.Mode.Kind.ShouldBe(FailKind.NodeError);
    }

    [Fact]
    public void Keyword_TwoModes_FirstOneWins()
    {
        var plan = Injector(MockMptTestHost.DefaultOptions).Resolve(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Mpt("a __fail_tts_failed__ b __fail_node_error__"));

        plan.Mode.Kind.ShouldBe(FailKind.TtsFailed);
    }

    [Fact]
    public void Keyword_FailImage_BeatsBlockImage_AndEnvReport()
    {
        ImageReportMode.TryParse("funds", out var funds, out var error).ShouldBeTrue(error);

        var plan = Injector(MockMptTestHost.DefaultOptions with { ImageReport = funds }).Resolve(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Mpt("__fail_image__", terms => terms[0] = terms[0]!.GetValue<string>() + " __block_image__"));

        plan.Image.Kind.ShouldBe(ImageReportKind.ModeratedAll);
    }

    [Fact]
    public void Alias_OverridingAnExplicitReport_IsLogged()
    {
        ImageReportMode.TryParse("funds", out var funds, out var error).ShouldBeTrue(error);
        var logger = new ListLogger();

        var plan = Injector(
            MockMptTestHost.DefaultOptions with { FailMode = new FailMode(FailKind.ImageModerated), ImageReport = funds },
            logger).Resolve(Guid.CreateVersion7(), Guid.CreateVersion7(), Mpt());

        plan.Image.Kind.ShouldBe(ImageReportKind.ModeratedAll);
        logger.Messages.ShouldContain(message => message.Contains("overrides", StringComparison.Ordinal));
    }

    [Fact]
    public void Alias_WithReportOk_DoesNotLogAnOverride()
    {
        var logger = new ListLogger();

        Injector(
            MockMptTestHost.DefaultOptions with { FailMode = new FailMode(FailKind.ImageModerated) },
            logger).Resolve(Guid.CreateVersion7(), Guid.CreateVersion7(), Mpt());

        logger.Messages.ShouldNotContain(message => message.Contains("overrides", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_UnknownKeywordName_NeverEchoedWithBodyText()
    {
        var logger = new ListLogger();

        Injector(MockMptTestHost.DefaultOptions with { FailMode = new FailMode(FailKind.Timeout) }, logger)
            .Resolve(Guid.CreateVersion7(), Guid.CreateVersion7(), Mpt("SECRET-QA-TEXT __fail_leak_me__"));

        logger.Messages.ShouldAllBe(message => !message.Contains("SECRET-QA-TEXT", StringComparison.Ordinal));
        logger.Messages.ShouldAllBe(message => !message.Contains("leak_me", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keyword_LostTask_FirstPollIs404()
    {
        await using var host = await MockMptTestHost.StartAsync();

        var taskId = await host.SubmitAsync(BodyWith("Câu một. __fail_lost_task__"));
        using var response = await host.GetAsync($"/v1/renders/{taskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await MockMptTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.TaskUnknown);
    }

    [Fact]
    public async Task Keyword_QualityCheck_FailsNamedCheck_AndUnknownCheckIsIgnored()
    {
        await using var host = await MockMptTestHost.StartAsync();

        var named = await TerminalAsync(host, BodyWith("Câu một. __fail_quality:loudness__"));
        var invented = await TerminalAsync(host, BodyWith("Câu một. __fail_quality:invented__"));

        named.State.ShouldBe(RenderStates.Succeeded);
        named.Check!.Value.GetProperty("loudness").GetProperty("after").GetProperty("integrated_lufs").GetDouble()
            .ShouldBe(-11.2);
        invented.State.ShouldBe(RenderStates.Succeeded);
        invented.Check!.Value.GetProperty("loudness").GetProperty("after").GetProperty("integrated_lufs").GetDouble()
            .ShouldNotBe(-11.2);
    }

    [Fact]
    public async Task Keyword_Timeout_NeverTerminalEvenWhenEnvIsDifferent()
    {
        var options = MockMptTestHost.DefaultOptions with { FailMode = new FailMode(FailKind.TtsFailed) };
        await using var host = await MockMptTestHost.StartAsync(options);

        var taskId = await host.SubmitAsync(BodyWith("Câu một. __fail_timeout__"));
        await Task.Delay(TimeSpan.FromSeconds(3 * options.RenderSeconds), TestContext.Current.CancellationToken);

        var status = await host.StatusAsync(taskId);
        status.State.ShouldBe(RenderStates.Running);
        status.Percent.ShouldBe(95);
    }

    [Fact]
    public async Task Rate_One_EveryHostSubmitFails_WithAFixedSetCode()
    {
        await using var host = await MockMptTestHost.StartAsync(MockMptTestHost.DefaultOptions with { FailRate = 1 });
        var allowed = new[]
        {
            RenderOutcomeCodes.TtsFailed,
            RenderOutcomeCodes.RenderAssemblyFailed,
            RenderOutcomeCodes.RenderNodeError,
        };

        for (var i = 0; i < 6; i++)
        {
            var id = SubmitBodies.Build(SubmitBodies.Landscape);
            var taskId = await host.SubmitAsync(id);
            using var response = await host.GetAsync($"/v1/renders/{taskId}");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue; // lost_task is in the rate set: the row is gone, which is its terminal shape
            }

            var status = (await host.PollUntilTerminalAsync(taskId))[^1];
            status.State.ShouldBe(RenderStates.Failed);
            allowed.ShouldContain(status.Outcome!.Code);
        }
    }

    [Fact]
    public async Task Rate_Zero_NeverInjects()
    {
        await using var host = await MockMptTestHost.StartAsync(MockMptTestHost.DefaultOptions with { FailRate = 0 });

        for (var i = 0; i < 3; i++)
        {
            (await TerminalAsync(host, SubmitBodies.LandscapeFresh())).State.ShouldBe(RenderStates.Succeeded);
        }
    }

    [Fact(Skip = "QA-2: FailureInjector.Resolve treats a lone `__block_image__` as a layer-1 keyword hit and drops MOCK_FAIL_MODE/MOCK_FAIL_RATE; enable when fixed")]
    public void BlockImage_AloneStillLetsTheEnvModeApply()
    {
        // Spec precedence: the env mode applies unless a *mode* keyword fired; `__block_image__` only shapes slots.
        var plan = Injector(MockMptTestHost.DefaultOptions with { FailMode = new FailMode(FailKind.TtsFailed) }).Resolve(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Mpt(terms: terms => terms[0] = terms[0]!.GetValue<string>() + " __block_image__"));

        plan.Mode.Kind.ShouldBe(FailKind.TtsFailed);
    }

    private sealed class ListLogger : ILogger<FailureInjector>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
