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
/// Failure injection (11.1.2-T2): every <c>MOCK_FAIL_MODE</c> reaches the terminal state and outcome of the 05 D5
/// table through <c>GET /v1/renders/{task_id}</c>, <c>MOCK_FAIL_RATE</c> is deterministic per
/// <c>render_job_id</c>, trigger keywords beat the environment, and the log never carries content.
/// </summary>
public sealed class FailureInjectionTests
{
    private static Func<string, string?> Reader(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static MockMptOptions WithMode(string mode)
    {
        FailModeParser.TryParse(mode, out var parsed, out var error).ShouldBeTrue(error);
        return MockMptTestHost.DefaultOptions with { FailMode = parsed };
    }

    private static string Body(string? script = null, Func<JsonArray, JsonArray>? terms = null)
    {
        var node = JsonNode.Parse(SubmitBodies.Build(SubmitBodies.Landscape, script: script))!.AsObject();
        if (terms is not null)
        {
            node["mpt"]!["video_terms"] = terms((JsonArray)node["mpt"]!["video_terms"]!);
        }

        return node.ToJsonString();
    }

    private static MptSubmitFields Mpt(string body) =>
        JsonSerializer.Deserialize<AdapterSubmitRequestV1>(body, RenderNodeJson.Strict)!.Mpt;

    private static async Task<RenderStatusV1> TerminalAsync(MockMptTestHost host, string body)
    {
        var taskId = await host.SubmitAsync(body);
        return (await host.PollUntilTerminalAsync(taskId))[^1];
    }

    [Theory]
    [InlineData("tts_failed", "tts_failed", "audio", "voice")]
    [InlineData("assembly_failed", "render_assembly_failed", "video", "assembling")]
    [InlineData("node_error", "render_node_error", "preflight", "queued")]
    [InlineData("image_moderated", "image_moderated", "materials", "images")]
    public async Task Mode_Failure_ReachesTheTableOutcome(string mode, string code, string detail, string stage)
    {
        await using var host = await MockMptTestHost.StartAsync(WithMode(mode));

        var status = await TerminalAsync(host, SubmitBodies.LandscapeFresh());

        status.State.ShouldBe(RenderStates.Failed);
        status.Stage.ShouldBe(stage);
        status.Outcome.ShouldNotBeNull();
        status.Outcome.Code.ShouldBe(code);
        status.Outcome.Detail.ShouldBe(detail);
        status.Outcome.Message.ShouldBe(OutcomeMessages.For(code));
        status.Mpt!.FailedStage.ShouldBe(detail);
    }

    [Fact]
    public async Task Mode_TtsFailed_OutcomeTtsFailed()
    {
        await using var host = await MockMptTestHost.StartAsync(WithMode("tts_failed"));

        var status = await TerminalAsync(host, SubmitBodies.LandscapeFresh());

        status.Outcome!.Code.ShouldBe(RenderOutcomeCodes.TtsFailed);
        status.Images.ShouldBeNull();
    }

    [Fact]
    public async Task Mode_AssemblyFailed_ReportsTheImagesOfTheFinishedStage()
    {
        await using var host = await MockMptTestHost.StartAsync(WithMode("assembly_failed"));

        var status = await TerminalAsync(host, SubmitBodies.LandscapeFresh());

        status.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderAssemblyFailed);
        status.Images.ShouldNotBeNull();
    }

    [Fact]
    public async Task Mode_Timeout_NeverTerminal()
    {
        var options = WithMode("timeout");
        await using var host = await MockMptTestHost.StartAsync(options);

        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());
        await Task.Delay(TimeSpan.FromSeconds(3 * options.RenderSeconds), TestContext.Current.CancellationToken);

        var status = await host.StatusAsync(taskId);
        status.State.ShouldBe(RenderStates.Running);
        status.Stage.ShouldBe(RenderStages.Assembling);
        status.Percent.ShouldBe(95);
    }

    [Fact]
    public async Task Mode_LostTask_PollReturns404()
    {
        await using var host = await MockMptTestHost.StartAsync(WithMode("lost_task"));

        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());
        using var response = await host.GetAsync($"/v1/renders/{taskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await MockMptTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.TaskUnknown);
    }

    [Fact]
    public async Task Mode_QualityCheck_FailsNamedCheck()
    {
        await using var host = await MockMptTestHost.StartAsync(WithMode("quality:loudness"));

        var status = await TerminalAsync(host, SubmitBodies.LandscapeFresh());

        status.State.ShouldBe(RenderStates.Succeeded);
        var after = status.Check!.Value.GetProperty("loudness").GetProperty("after").GetProperty("integrated_lufs");
        after.GetDouble().ShouldBe(-11.2);
    }

    [Theory]
    [InlineData("quality:invented")]
    [InlineData("quality:container_valid")]
    public void Mode_QualityUnknownCheck_Exits2(string mode)
    {
        var options = WithMode(mode);

        var exception = Should.Throw<MockStartupException>(() => MockHost.Create(options, "http://127.0.0.1:0"));

        exception.Message.ShouldContain(mode);
        exception.Message.ShouldContain("loudness");
    }

    [Fact]
    public void Mode_Unknown_Exits2()
    {
        MockMptEnvironment
            .TryRead(Reader(("MOCK_FAIL_MODE", "nonsense")), FixtureFiles.Root, out _, out var error)
            .ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("nonsense");
        error.ShouldContain("tts_failed");
        error.ShouldContain("quality:<check>");
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("abc")]
    public void Rate_OutOfRange_Exits2(string raw)
    {
        MockMptEnvironment
            .TryRead(Reader(("MOCK_FAIL_RATE", raw)), FixtureFiles.Root, out _, out var error)
            .ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("MOCK_FAIL_RATE");
    }

    [Fact]
    public void ImageReport_InvalidValue_Exits2()
    {
        MockMptEnvironment
            .TryRead(Reader(("MOCK_IMAGE_REPORT", "garbage")), FixtureFiles.Root, out _, out var error)
            .ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("garbage");
    }

    [Fact]
    public void Rate_Deterministic_ExactCount()
    {
        var ids = Enumerable.Range(0, 100).Select(i => Guid.Parse($"00000000-0000-7000-8000-{i:D12}")).ToList();

        var first = ids.Select(id => FailRate.Select(0.5, id)).ToList();
        var second = ids.Select(id => FailRate.Select(0.5, id)).ToList();

        first.Count(mode => mode is not null).ShouldBe(50);
        second.ShouldBe(first);
        first.Where(mode => mode is not null).ShouldAllBe(mode => FailRate.Set.Contains(mode!.Value));
        ids.Count(id => FailRate.Select(0, id) is not null).ShouldBe(0);
        ids.Count(id => FailRate.Select(1, id) is not null).ShouldBe(100);
    }

    [Fact]
    public void Rate_WithMode_IsInert()
    {
        using var host = new InjectorFixture(MockMptTestHost.DefaultOptions with
        {
            FailRate = 1,
            FailMode = new FailMode(FailKind.TtsFailed),
        });

        var plan = host.Injector.Resolve(Guid.CreateVersion7(), Guid.CreateVersion7(), Mpt(Body()));

        plan.Source.ShouldBe(FailureInjector.SourceEnv);
        plan.Mode.Kind.ShouldBe(FailKind.TtsFailed);
    }

    [Fact]
    public void Rate_Fires_WhenNoKeywordNorMode()
    {
        using var host = new InjectorFixture(MockMptTestHost.DefaultOptions with { FailRate = 1 });

        var plan = host.Injector.Resolve(Guid.CreateVersion7(), Guid.CreateVersion7(), Mpt(Body()));
        var withKeyword = host.Injector.Resolve(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Mpt(Body(script: "x __fail_timeout__")));

        plan.Source.ShouldBe(FailureInjector.SourceRate);
        withKeyword.Source.ShouldBe(FailureInjector.SourceKeyword);
        withKeyword.Mode.Kind.ShouldBe(FailKind.Timeout);
    }

    [Fact]
    public async Task Keyword_BeatsEnvMode()
    {
        await using var host = await MockMptTestHost.StartAsync(WithMode("tts_failed"));

        var status = await TerminalAsync(host, Body(script: "Câu một. __fail_assembly_failed__"));

        status.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderAssemblyFailed);
    }

    [Fact]
    public async Task Keyword_UnknownName_IsIgnored()
    {
        await using var host = await MockMptTestHost.StartAsync(MockMptTestHost.DefaultOptions);

        var status = await TerminalAsync(host, Body(script: "Câu một. __fail_nonsense__ Hết."));

        status.State.ShouldBe(RenderStates.Succeeded);
    }

    [Fact]
    public async Task Keyword_FailImage_ModeratedAll()
    {
        await using var host = await MockMptTestHost.StartAsync(MockMptTestHost.DefaultOptions);

        var status = await TerminalAsync(host, Body(script: "Câu một. __fail_image__"));

        status.State.ShouldBe(RenderStates.Failed);
        status.Outcome!.Code.ShouldBe(RenderOutcomeCodes.ImageModerated);
        var failed = status.Images!.Value.GetProperty("failed").EnumerateArray().ToList();
        failed.Count.ShouldBe(4);
        failed.ShouldAllBe(entry => entry.GetProperty("error_code").GetString() == "moderation_blocked");
    }

    [Fact]
    public async Task Keyword_BlockImage_OneSlot()
    {
        await using var host = await MockMptTestHost.StartAsync(MockMptTestHost.DefaultOptions);

        var status = await TerminalAsync(
            host,
            Body(terms: terms =>
            {
                terms[1] = terms[1]!.GetValue<string>() + " __block_image__";
                return terms;
            }));

        status.State.ShouldBe(RenderStates.Succeeded);
        var failed = status.Images!.Value.GetProperty("failed").EnumerateArray().ToList();
        failed.Count.ShouldBe(1);
        failed[0].GetProperty("slot").GetInt32().ShouldBe(1);
        failed[0].GetProperty("error_code").GetString().ShouldBe("moderation_blocked");
    }

    [Fact]
    public async Task ImageReport_ImageModeratedAlias_EqualsModeratedAll()
    {
        await using var alias = await MockMptTestHost.StartAsync(WithMode("image_moderated"));
        await using var report = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { ImageReport = ImageReportMode.ModeratedAll });

        var viaAlias = await TerminalAsync(alias, SubmitBodies.LandscapeFresh());
        var viaReport = await TerminalAsync(report, SubmitBodies.LandscapeFresh());

        var left = JsonSerializer.Serialize(viaAlias with { TaskId = Guid.Empty, RenderJobId = Guid.Empty }, RenderNodeJson.Strict);
        var right = JsonSerializer.Serialize(viaReport with { TaskId = Guid.Empty, RenderJobId = Guid.Empty }, RenderNodeJson.Strict);
        var difference = JsonComparison.Difference(left, right);
        difference.ShouldBeNull(difference);
    }

    [Theory]
    [InlineData("funds", RenderOutcomeCodes.ImageGenerationFailed)]
    [InlineData("moderated_all", RenderOutcomeCodes.ImageModerated)]
    public async Task ImageReport_ForcingValues_FailMaterials(string value, string code)
    {
        ImageReportMode.TryParse(value, out var mode, out var error).ShouldBeTrue(error);
        await using var host = await MockMptTestHost.StartAsync(MockMptTestHost.DefaultOptions with { ImageReport = mode });

        var status = await TerminalAsync(host, SubmitBodies.LandscapeFresh());

        status.Outcome!.Code.ShouldBe(code);
        status.Outcome.Detail.ShouldBe("materials");
    }

    [Fact]
    public async Task ImageReport_NonForcingValues_StillSucceed()
    {
        ImageReportMode.TryParse("moderated:0,2", out var mode, out var error).ShouldBeTrue(error);
        await using var host = await MockMptTestHost.StartAsync(MockMptTestHost.DefaultOptions with { ImageReport = mode });

        var status = await TerminalAsync(host, SubmitBodies.LandscapeFresh());

        status.State.ShouldBe(RenderStates.Succeeded);
        status.Images!.Value.GetProperty("failed").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void Log_NoContentFields()
    {
        var logger = new CapturingLogger<FailureInjector>();
        using var fixture = new InjectorFixture(
            MockMptTestHost.DefaultOptions with { FailMode = new FailMode(FailKind.TtsFailed) },
            logger);
        var taskId = Guid.CreateVersion7();
        const string secret = "TOP-SECRET-SCRIPT-TEXT";

        fixture.Injector.Resolve(taskId, Guid.CreateVersion7(), Mpt(Body(script: secret + " __fail_node_error__")));

        var line = logger.Messages.ShouldHaveSingleItem();
        line.ShouldContain(taskId.ToString());
        line.ShouldContain("node_error");
        line.ShouldContain("keyword");
        line.ShouldNotContain(secret);
        line.ShouldNotContain("styled scene prompt");
    }

    private sealed class InjectorFixture : IDisposable
    {
        internal InjectorFixture(MockMptOptions options, ILogger<FailureInjector>? logger = null)
        {
            var fixtures = FixtureSet.Load(FixtureFiles.Root);
            Injector = new FailureInjector(options, fixtures, logger ?? new CapturingLogger<FailureInjector>());
        }

        internal FailureInjector Injector { get; }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
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
