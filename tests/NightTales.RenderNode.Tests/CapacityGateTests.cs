using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The capacity rule (05.4.1-T2 §4, F07/C10) at the seam: "count non-terminal + insert" under one permit, so N
/// simultaneous submits can never insert more rows than the free slots.
/// </summary>
public sealed class CapacityGateTests
{
    [Fact]
    public async Task ConcurrentSubmits_InsertsOnce()
    {
        var store = new InMemoryTaskStore();
        using var gate = Gate(store, maxConcurrentTasks: 1);
        var candidates = Enumerable
            .Range(0, 3)
            .Select(_ => Records.Queued(Records.NewTaskId(), Guid.CreateVersion7()))
            .ToArray();

        var results = await Task.WhenAll(candidates.Select(candidate => gate.TryEnterAsync(candidate, CancellationToken.None)));

        results.Count(result => !result.IsError).ShouldBe(1);
        results.Count(result => result.IsError && result.FirstError.Code == AdapterErrorCodes.CapacityFull).ShouldBe(2);
        (await store.CountNonTerminalAsync(CancellationToken.None)).ShouldBe(1);
    }

    [Fact]
    public async Task TerminalRows_DoNotOccupyASlot()
    {
        var store = new InMemoryTaskStore();
        await store.InsertAsync(Records.Succeeded(Records.NewTaskId(), Guid.CreateVersion7()), CancellationToken.None);
        using var gate = Gate(store, maxConcurrentTasks: 1);

        var entered = await gate.TryEnterAsync(Records.Queued(Records.NewTaskId(), Guid.CreateVersion7()), CancellationToken.None);

        entered.IsError.ShouldBeFalse();
        (await store.CountNonTerminalAsync(CancellationToken.None)).ShouldBe(1);
    }

    [Fact]
    public async Task SameRenderJobId_IsAnIdempotencyConflict()
    {
        var store = new InMemoryTaskStore();
        using var gate = Gate(store, maxConcurrentTasks: 2);
        var renderJobId = Guid.CreateVersion7();

        var first = await gate.TryEnterAsync(Records.Queued(Records.NewTaskId(), renderJobId), CancellationToken.None);
        var second = await gate.TryEnterAsync(Records.Queued(Records.NewTaskId(), renderJobId), CancellationToken.None);

        first.IsError.ShouldBeFalse();
        second.IsError.ShouldBeTrue();
        second.FirstError.Code.ShouldBe(AdapterErrorCodes.IdempotencyConflict);
    }

    private static CapacityGate Gate(ITaskStore store, int maxConcurrentTasks) =>
        new(store, Options.Create(new RenderNodeServerOptions { ApiKey = new string('k', 32), MaxConcurrentTasks = maxConcurrentTasks }));
}
