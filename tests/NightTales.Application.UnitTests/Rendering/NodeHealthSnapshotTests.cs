using System.Text.Json;
using NightTales.Application.Abstractions.Rendering;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// The <c>render-node-health.v1</c> fixture replay of 11.3.3-T1 (11 README D13, 05 README D4/D14): every
/// fixture the adapter and mock-mpt serve is read through the tolerant wire reader the poller uses
/// (<see cref="RenderNodeJson.Tolerant"/>) and projected through <see cref="NodeHealthSnapshot.From"/>
/// into the cache shape the selector and the gauges read. A renamed or re-typed member fails here first.
/// </summary>
public sealed class NodeHealthSnapshotTests
{
    private const string NodeName = "mock";
    private static readonly DateTimeOffset PulledAt = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Every shipped health fixture, so a new one is replayed without editing this list.</summary>
    public static TheoryData<string> HealthFixtures { get; } = FixtureData();

    [Theory]
    [MemberData(nameof(HealthFixtures))]
    public void EveryFixture_ParsesAndProjectsEveryCachedField(string file)
    {
        var health = Read(file);

        var snapshot = NodeHealthSnapshot.From(NodeName, health, PulledAt, httpOk: health.Status == "ok");

        snapshot.NodeName.ShouldBe(NodeName);
        snapshot.PulledAt.ShouldBe(PulledAt);
        snapshot.Status.ShouldBe(health.Status);
        snapshot.MaxConcurrentTasks.ShouldBe(health.Capacity.MaxConcurrentTasks);
        snapshot.ActiveTasks.ShouldBe(health.Capacity.ActiveTasks);
        snapshot.Accepting.ShouldBe(health.Capacity.Accepting);
        snapshot.GpuUtilPct.ShouldBe(health.Gpu.UtilPct);
        snapshot.GpuMemUsedMb.ShouldBe(health.Gpu.MemUsedMb);
        snapshot.DiskUsedPct.ShouldBe(health.Disk.UsedPct);

        if (health.Status == "ok")
        {
            snapshot.Reasons.ShouldBeEmpty();
            snapshot.Accepting.ShouldBeTrue();
        }
        else
        {
            snapshot.Reasons.ShouldNotBeEmpty();
            snapshot.Accepting.ShouldBeFalse();
        }
    }

    [Fact]
    public void OkFixture_ProjectsTheHealthySnapshot()
    {
        var snapshot = Replay("render-node-health.v1.ok.json");

        snapshot.HttpOk.ShouldBeTrue();
        snapshot.Status.ShouldBe("ok");
        snapshot.Reasons.ShouldBeEmpty();
        snapshot.MaxConcurrentTasks.ShouldBe(2);
        snapshot.ActiveTasks.ShouldBe(1);
        snapshot.Accepting.ShouldBeTrue();
        snapshot.GpuUtilPct.ShouldBe(12);
        snapshot.GpuMemUsedMb.ShouldBe(2048);
        snapshot.DiskUsedPct.ShouldBe(41);
    }

    [Fact]
    public void ProxyDownFixture_KeepsTheReason_AndIsNotDispatchable()
    {
        var snapshot = Replay("render-node-health.v1.proxy-down.json");

        snapshot.HttpOk.ShouldBeFalse();
        snapshot.Status.ShouldBe("error");
        snapshot.Reasons.ShouldBe(["tts_proxy_not_ok"]);
        snapshot.Accepting.ShouldBeFalse();
    }

    [Fact]
    public void DiskLowFixture_KeepsTheReason_AndTheDiskReading()
    {
        var snapshot = Replay("render-node-health.v1.disk-low.json");

        snapshot.HttpOk.ShouldBeFalse();
        snapshot.Status.ShouldBe("error");
        snapshot.Reasons.ShouldBe(["disk_low"]);
        snapshot.DiskUsedPct.ShouldBe(91);
        snapshot.Accepting.ShouldBeFalse();
    }

    private static NodeHealthSnapshot Replay(string file)
    {
        var health = Read(file);

        return NodeHealthSnapshot.From(NodeName, health, PulledAt, httpOk: health.Status == "ok");
    }

    /// <summary>Reads a fixture the way the poller does: the tolerant reader, never the strict one.</summary>
    private static RenderNodeHealthV1 Read(string file) =>
        JsonSerializer.Deserialize<RenderNodeHealthV1>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file)),
            RenderNodeJson.Tolerant)
        ?? throw new InvalidOperationException($"'{file}' deserialised to null");

    private static TheoryData<string> FixtureData()
    {
        var data = new TheoryData<string>();
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures");
        foreach (var file in Directory.GetFiles(directory, "render-node-health.v1.*.json")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal))
        {
            data.Add(file!);
        }

        return data;
    }
}
