using System.Net;
using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// The <c>MOCK_*</c> environment contract of 11.1.2-T1 (Design, Failure handling): every unusable value is a
/// named rejection, never a listener that opens with a value nobody can use, and the capacity the core reports
/// is the configured one.
/// </summary>
public sealed class EnvironmentTests
{
    private static Func<string, string?> Reader(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void Environment_Defaults_AreApplied()
    {
        MockMptEnvironment
            .TryRead(Reader(), FixtureFiles.Root, out var options, out var error)
            .ShouldBeTrue(error);

        options.ApiKey.ShouldBe(MockMptOptions.DefaultApiKey);
        options.RenderSeconds.ShouldBe(20);
        options.VideoSeconds.ShouldBe(10);
        options.MaxConcurrentTasks.ShouldBe(2);
        options.Fixture.ShouldBeNull();
        options.FixturesDirectory.ShouldEndWith(Path.Combine("worker-node", "contract", "fixtures"));
        options.MediaDirectory.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Environment_MockNameWinsOverTheCoreSpelling()
    {
        MockMptEnvironment
            .TryRead(
                Reader(
                    ("MOCK_MAX_CONCURRENT", "4"),
                    ("RenderNodeServer__MaxConcurrentTasks", "2"),
                    ("MOCK_API_KEY", MockMptOptions.DefaultApiKey)),
                FixtureFiles.Root,
                out var options,
                out var error)
            .ShouldBeTrue(error);

        options.MaxConcurrentTasks.ShouldBe(4);
    }

    [Fact]
    public void Environment_CoreSpellingIsAcceptedAlone()
    {
        MockMptEnvironment
            .TryRead(
                Reader(
                    ("RenderNodeServer__MaxConcurrentTasks", "3"),
                    ("RenderNodeServer__ApiKey", MockMptOptions.DefaultApiKey)),
                FixtureFiles.Root,
                out var options,
                out var error)
            .ShouldBeTrue(error);

        options.MaxConcurrentTasks.ShouldBe(3);
        options.ApiKey.ShouldBe(MockMptOptions.DefaultApiKey);
    }

    [Fact]
    public void Environment_RenderSecondsZero_IsRejected()
    {
        MockMptEnvironment
            .TryRead(Reader(("MOCK_RENDER_SECONDS", "0")), FixtureFiles.Root, out _, out var error)
            .ShouldBeFalse();

        error!.ShouldContain("MOCK_RENDER_SECONDS");
    }

    [Fact]
    public void Environment_MaxConcurrentBelowOne_IsRejected()
    {
        MockMptEnvironment
            .TryRead(Reader(("MOCK_MAX_CONCURRENT", "0")), FixtureFiles.Root, out _, out var error)
            .ShouldBeFalse();

        error!.ShouldContain("MOCK_MAX_CONCURRENT");
    }

    [Fact]
    public void Environment_ShortApiKey_IsRejected()
    {
        MockMptEnvironment
            .TryRead(Reader(("MOCK_API_KEY", "short")), FixtureFiles.Root, out _, out var error)
            .ShouldBeFalse();

        error!.ShouldContain("MOCK_API_KEY");
    }

    [Theory]
    [InlineData("MOCK_RENDER_SECONDS", "abc")]
    [InlineData("MOCK_RENDER_SECONDS", "-2")]
    [InlineData("MOCK_VIDEO_SECONDS", "1.5")]
    [InlineData("MOCK_VIDEO_SECONDS", "0")]
    [InlineData("MOCK_MAX_CONCURRENT", "many")]
    [InlineData("MOCK_MAX_CONCURRENT", "-1")]
    public void Environment_NonIntegerOrBelowOne_IsRejected(string name, string value)
    {
        // Every numeric MOCK_* row fails to a named message when it is not an integer ≥ 1 — never a listener
        // that opens on a value nobody can use (Failure handling, §10 T32).
        MockMptEnvironment
            .TryRead(Reader((name, value)), FixtureFiles.Root, out _, out var error)
            .ShouldBeFalse($"{name}={value} must be rejected");

        error!.ShouldContain(name);
    }

    [Fact]
    public void Environment_ApiKeyExactly32Bytes_IsAccepted()
    {
        // The 05 D15 S3 floor: exactly 32 bytes is the shortest accepted key.
        MockMptEnvironment
            .TryRead(
                Reader(("MOCK_API_KEY", new string('k', 32))),
                FixtureFiles.Root,
                out var options,
                out var error)
            .ShouldBeTrue(error);

        options.ApiKey.ShouldBe(new string('k', 32));
    }

    [Theory]
    [InlineData("render-status.v1.succeeded", "render-status.v1.succeeded")]
    [InlineData("render-status.v1.succeeded.json", "render-status.v1.succeeded")]
    [InlineData("  render-status.v1.queued  ", "render-status.v1.queued")]
    public void Environment_FixtureId_IsNormalized(string raw, string expected)
    {
        MockMptEnvironment
            .TryRead(Reader(("MOCK_FIXTURE", raw)), FixtureFiles.Root, out var options, out var error)
            .ShouldBeTrue(error);

        options.Fixture.ShouldBe(expected);
    }
    [Fact]
    public void Environment_UnresolvableFixturesDirectory_IsRejected()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nts-mock-mpt-tests", "missing-fixtures");

        MockMptEnvironment
            .TryRead(Reader(("MOCK_FIXTURES_DIR", missing)), FixtureFiles.Root, out var options, out var error)
            .ShouldBeTrue(error);

        // The path itself is validated by the loader, which is the component that must read it.
        Should.Throw<MockStartupException>(() => MockHost.Create(options, "http://127.0.0.1:0"))
            .Message.ShouldContain(missing);
    }

    [Fact]
    public void Environment_UnknownFixture_IsRejected()
    {
        var options = MockMptTestHost.DefaultOptions with { Fixture = "render-status.v1.nope" };

        Should.Throw<MockStartupException>(() => MockHost.Create(options, "http://127.0.0.1:0"))
            .Message.ShouldContain("render-status.v1.nope");
    }

    [Fact]
    public async Task Health_CapacityMaxMatchesSetting()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { MaxConcurrentTasks = 3 });

        using var response = await host.GetAsync(RenderNodeRoutes.Health);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var health = await MockMptTestHost.ReadAsync<RenderNodeHealthV1>(response);
        health.Capacity.MaxConcurrentTasks.ShouldBe(3);
        health.Capacity.Accepting.ShouldBeTrue();
        health.Capacity.ActiveTasks.ShouldBe(0);
    }

    [Fact]
    public async Task Health_ActiveTasks_CountsNonTerminalTasks()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 60, MaxConcurrentTasks = 2 });

        await host.SubmitAsync(SubmitBodies.LandscapeFresh());

        using var response = await host.GetAsync(RenderNodeRoutes.Health);
        var health = await MockMptTestHost.ReadAsync<RenderNodeHealthV1>(response);

        health.Capacity.ActiveTasks.ShouldBe(1);
        health.Capacity.Accepting.ShouldBeTrue();
    }
}
