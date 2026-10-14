using ErrorOr;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Review;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// The scripted provider edge shared by the story-generation tests added for 02.3.3-T1 (the same shape the
/// 02.2.2 job harnesses nest privately): the writer, the prompt assembler, the cost estimator and the realtime
/// notifier, so a test over the real Worker graph never reaches the network. Concrete 02.3.3-T1 cases live in
/// <c>TrueCrimeGenerationFlowTests</c> and <c>JobRecheckFactSheetChangeTests</c>.
/// </summary>
internal sealed class ScriptedStoryWriter : IStoryWriter
{
    /// <summary>How many times the paid writer call was reached; a refusal at step 0 keeps it at zero.</summary>
    public int Calls { get; private set; }

    public ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct)
    {
        Calls++;

        return ValueTask.FromResult(TextCallResult<StoryContentV1>.Ok(FakeStory.Draft(), "gpt-test", null, "req-test"));
    }

    public ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct) =>
        throw new InvalidOperationException("The review stage is disabled in these tests.");

    public ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct) =>
        throw new InvalidOperationException("The revision stage is disabled in these tests.");
}

/// <summary>The draft the scripted writer returns (02 README D5): a valid <c>story.v1</c> document, no provider contact.</summary>
internal static class FakeStory
{
    /// <summary>A marker no log line, error detail or payload is allowed to carry (D19).</summary>
    internal const string Marker = "FAKESTORYMARKERIBIS";

    internal static StoryContentV1 Draft() => new(
        $"Tieu de {Marker}",
        new StorySegmentV1($"Mo dau {Marker}.", ["k1"], []),
        [new StorySegmentV1($"Doan mot {Marker}.", ["k2"], [])],
        new StorySegmentV1($"Ket {Marker}.", ["k3"], []),
        "Mo ta.",
        ["tag"],
        [],
        new StoryStructureV1("a", "b", "c", "d", "e"));
}

/// <summary>The prompt assembler with fixed strings: no catalog, no I/O.</summary>
internal sealed class StubPromptAssembler : IPromptAssembler
{
    public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx) => new("system", "{}", "pv-test", "cache");

    public ErrorOr<AssembledPrompt> Review(StoryReviewRequest req) => new AssembledPrompt("system", "{}", "pv-review", "cache");

    public ErrorOr<AssembledPrompt> Revise(StoryRevisionRequest req) => new AssembledPrompt("system", "{}", "pv-revise", "cache");
}

/// <summary>A fixed, price-catalog-free worst case so the budget guard reserves deterministically.</summary>
internal sealed class FixedStoryCostEstimator : IStoryCostEstimator
{
    public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
        Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));

    public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
        WorstCaseAsync(tier, language, ct);

    public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
        WorstCaseAsync(tier, language, ct);

    public Task<ErrorOr<UsageBound>> RevisionWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
        WorstCaseAsync(tier, language, ct);
}

/// <summary>Drops realtime events: these tests assert on persisted state, not on pushes.</summary>
internal sealed class NullStoryNotifier : IRealtimeNotifier
{
    public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
