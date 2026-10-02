using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.MockMpt.Fixtures;
using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// The D14 fixture replay (11.1.2-T1 Design/Tests): <c>MOCK_FIXTURE=&lt;id&gt;</c> makes every task replay that
/// status fixture and the health source replay a health fixture. The served body equals the fixture except the
/// fields the contract generates for the replaying task — <c>task_id</c>, <c>render_job_id</c> and timestamps
/// (the ignore list lives in <see cref="JsonComparison"/>).
/// </summary>
public sealed class FixtureReplayTests
{
    public static TheoryData<string> StatusFixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in FixtureFiles.All().Where(name => name.StartsWith("render-status.v1.", StringComparison.Ordinal)))
            {
                data.Add(id);
            }

            return data;
        }
    }

    public static TheoryData<string, int> HealthFixtures => new()
    {
        { "render-node-health.v1.ok", (int)HttpStatusCode.OK },
        { "render-node-health.v1.proxy-down", (int)HttpStatusCode.ServiceUnavailable },
        { "render-node-health.v1.disk-low", (int)HttpStatusCode.ServiceUnavailable },
    };

    [Theory]
    [MemberData(nameof(StatusFixtures))]
    public async Task Status_Fixture_ReplaysVerbatim(string id)
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { Fixture = id });

        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());
        using var response = await host.GetAsync($"/v1/renders/{taskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var actual = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var difference = JsonComparison.Difference(FixtureFiles.Read(id), actual);
        difference.ShouldBeNull($"{id}: {difference}");
    }

    [Theory]
    [MemberData(nameof(HealthFixtures))]
    public async Task Health_Fixture_ReplaysVerbatim(string id, int statusCode)
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { Fixture = id });

        using var response = await host.GetAsync(RenderNodeRoutes.Health);

        ((int)response.StatusCode).ShouldBe(statusCode);
        var actual = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var difference = JsonComparison.Difference(FixtureFiles.Read(id), actual);
        difference.ShouldBeNull($"{id}: {difference}");
    }

    [Fact]
    public async Task Status_SucceededReplay_ServesTheGeneratedSubtitles()
    {
        // The succeeded fixture advertises a non-null output.subtitles, so the download must exist (defect D2:
        // the submit's `__fixture_<id>__` keyword replays it, exactly the QA repro).
        await using var host = await MockMptTestHost.StartAsync(MockMptTestHost.DefaultOptions);

        const string script = "Câu một. Câu hai! __fixture_render-status.v1.succeeded__";
        var taskId = await host.SubmitAsync(SubmitBodies.Build(SubmitBodies.Landscape, script: script));

        var status = await host.StatusAsync(taskId);
        status.State.ShouldBe(RenderStates.Succeeded);
        status.Output!.Subtitles.ShouldNotBeNull();

        using var response = await host.GetAsync($"/v1/renders/{taskId}/subtitles");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-subrip");
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        bytes.Length.ShouldBeGreaterThan(0);
        response.Content.Headers.ContentLength.ShouldBe(bytes.Length);
        response.Headers.GetValues(RenderNodeHeaders.ContentSha256).Single()
            .ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public async Task Status_Fixture_KeywordBeatsTheEnvironment()
    {
        // The keyword on the submitted body selects the fixture for that submit (11.1.2-T1 Design).
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { Fixture = "render-status.v1.succeeded" });

        var body = SubmitBodies.Build(
            SubmitBodies.Landscape,
            script: "NTS_FIXTURE_SCRIPT __fixture_render-status.v1.running-images__");
        var taskId = await host.SubmitAsync(body);

        using var response = await host.GetAsync($"/v1/renders/{taskId}");
        var actual = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var difference = JsonComparison.Difference(FixtureFiles.Read("render-status.v1.running-images"), actual);
        difference.ShouldBeNull($"{difference}");
    }

    [Fact]
    public void Fixtures_AllDeserializeWithStrictJson()
    {
        var ids = FixtureFiles.All();
        ids.Count.ShouldBeGreaterThanOrEqualTo(24);

        foreach (var id in ids)
        {
            var text = FixtureFiles.Read(id);

            // Strict is the reader the node and the mock use: unknown members and duplicates are rejected, so
            // parsing every fixture with it proves the frozen set stays readable (11 README D18).
            var node = JsonSerializer.Deserialize<JsonNode>(text, RenderNodeJson.Strict);
            node.ShouldNotBeNull(id);

            if (id.StartsWith("render-status.v1.", StringComparison.Ordinal))
            {
                JsonSerializer.Deserialize<RenderStatusV1>(text, RenderNodeJson.Strict).ShouldNotBeNull(id);
            }
            else if (id.StartsWith("render-node-health.v1.", StringComparison.Ordinal))
            {
                JsonSerializer.Deserialize<RenderNodeHealthV1>(text, RenderNodeJson.Strict).ShouldNotBeNull(id);
            }
            else if (id.StartsWith("render-check.v1.", StringComparison.Ordinal))
            {
                JsonSerializer.Deserialize<RenderCheckV1>(text, RenderNodeJson.Strict).ShouldNotBeNull(id);
            }
            else if (id.StartsWith("render-submit.v1.", StringComparison.Ordinal))
            {
                JsonSerializer.Deserialize<AdapterSubmitRequestV1>(text, RenderNodeJson.Strict).ShouldNotBeNull(id);
            }
        }

        // The mock's own loader accepts the whole directory, so a fixture the mock cannot serve fails here.
        var fixtures = FixtureSet.Load(FixtureFiles.Root);
        fixtures.StatusIds.Count.ShouldBe(ids.Count(name => name.StartsWith("render-status.v1.", StringComparison.Ordinal)));
        fixtures.HealthIds.Count.ShouldBe(ids.Count(name => name.StartsWith("render-node-health.v1.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Status_UnknownKeyword_RunsTheNormalPath()
    {
        // A __fixture_ name that matches no D14 fixture is ignored (the keyword can never fail a submit):
        // the task walks the clock like any other submit.
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 1 });

        var taskId = await host.SubmitAsync(
            SubmitBodies.Build(
                SubmitBodies.Landscape,
                script: "Script __fixture_render-status.v1.nope__ text."));
        var status = (await host.PollUntilTerminalAsync(taskId))[^1];

        status.State.ShouldBe(RenderStates.Succeeded);
        status.Percent.ShouldBe(100);
        status.Output.ShouldNotBeNull();
    }

    [Fact]
    public async Task Status_KeywordInVideoTerms_Replays()
    {
        // The trigger is scanned in video_terms too, not only video_script (FixtureKeyword.Find).
        await using var host = await MockMptTestHost.StartAsync();
        var node = JsonNode.Parse(SubmitBodies.LandscapeFresh())!.AsObject();
        node["mpt"]!["video_terms"] = new JsonArray("__fixture_render-status.v1.running-voice__");

        var taskId = await host.SubmitAsync(node.ToJsonString());
        using var response = await host.GetAsync($"/v1/renders/{taskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await MockMptTestHost.ReadAsync<RenderStatusV1>(response);
        status.State.ShouldBe(RenderStates.Running);
        status.Stage.ShouldBe(RenderStages.Voice);
    }

    [Fact]
    public async Task Status_KeywordWithoutSuffix_IsIgnored()
    {
        // "__fixture_" without the closing "__" is free text, not a trigger: the task runs normally.
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 1 });

        var taskId = await host.SubmitAsync(
            SubmitBodies.Build(
                SubmitBodies.Landscape,
                script: "The word __fixture_ starts but never closes."));
        var status = (await host.PollUntilTerminalAsync(taskId))[^1];

        status.State.ShouldBe(RenderStates.Succeeded);
    }

    [Fact]
    public void Fixtures_EmptyDirectory_IsNamedStartupFailure()
    {
        var empty = System.IO.Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "nts-mock-mpt-tests", "empty-fixtures")).FullName;

        var exception = Should.Throw<MockStartupException>(() => FixtureSet.Load(empty));
        exception.Message.ShouldContain(empty);
    }

    [Fact]
    public void Fixtures_MalformedFile_IsNamedStartupFailure()
    {
        var directory = System.IO.Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "nts-mock-mpt-tests", "bad-fixtures")).FullName;
        File.WriteAllText(Path.Combine(directory, "render-status.v1.broken.json"), "{ not json");

        var exception = Should.Throw<MockStartupException>(() => FixtureSet.Load(directory));
        exception.Message.ShouldContain("render-status.v1.broken");
    }

    [Fact]
    public void FixtureKeyword_Find_ReturnsTheFirstWellFormedToken()
    {
        // Whole-token and case-sensitive: the first well-formed token wins; a dangling prefix is skipped.
        FixtureKeyword.Find("a __fixture_x y__ b __fixture_render-status.v1.queued__ c")
            .ShouldBe("render-status.v1.queued");
        FixtureKeyword.Find("__FIXTURE_render-status.v1.queued__").ShouldBeNull();
        FixtureKeyword.Find("no token at all").ShouldBeNull();
    }
}
