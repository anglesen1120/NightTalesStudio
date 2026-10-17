using NightTales.Application.Rendering;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Rendering;

/// <summary>
/// 09.6.3-T1: the <c>Render</c> section refuses a value that would silently disable a guard of the poller (a zero
/// batch claims nothing, a zero poll interval spins), and accepts every bound inclusively. Plain unit test: no
/// host, no database.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RenderOptionsValidatorTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        RenderOptionsValidator.Validate(new RenderOptions()).ShouldBeEmpty();
    }

    [Fact]
    public void Bounds_AreInclusive()
    {
        var options = new RenderOptions
        {
            PollSeconds = RenderOptionsValidator.MaxPollSeconds,
            MaxMinutes = RenderOptionsValidator.MaxRenderMinutes,
            QueueAlertMinutes = RenderOptionsValidator.MaxQueueAlertMinutes,
            PollBatchSize = RenderOptionsValidator.MaxPollBatchSize,
            PollParallelism = RenderOptionsValidator.MaxPollParallelism,
        };

        RenderOptionsValidator.Validate(options).ShouldBeEmpty();

        var lowest = new RenderOptions
        {
            PollSeconds = 1,
            MaxMinutes = 1,
            QueueAlertMinutes = 1,
            PollBatchSize = 1,
            PollParallelism = 1,
        };

        RenderOptionsValidator.Validate(lowest).ShouldBeEmpty();
    }

    [Fact]
    public void EachOutOfBoundValue_IsReported_WithItsOwnName()
    {
        var cases = new (string Name, Action<RenderOptions> Break)[]
        {
            ("PollSeconds", options => options.PollSeconds = 0),
            ("PollSeconds", options => options.PollSeconds = RenderOptionsValidator.MaxPollSeconds + 1),
            ("MaxMinutes", options => options.MaxMinutes = 0),
            ("MaxMinutes", options => options.MaxMinutes = RenderOptionsValidator.MaxRenderMinutes + 1),
            ("QueueAlertMinutes", options => options.QueueAlertMinutes = 0),
            ("QueueAlertMinutes", options => options.QueueAlertMinutes = RenderOptionsValidator.MaxQueueAlertMinutes + 1),
            ("PollBatchSize", options => options.PollBatchSize = -1),
            ("PollBatchSize", options => options.PollBatchSize = RenderOptionsValidator.MaxPollBatchSize + 1),
            ("PollParallelism", options => options.PollParallelism = 0),
            ("PollParallelism", options => options.PollParallelism = RenderOptionsValidator.MaxPollParallelism + 1),
        };

        foreach (var (name, broken) in cases)
        {
            var options = new RenderOptions();
            broken(options);

            var problems = RenderOptionsValidator.Validate(options);

            problems.ShouldHaveSingleItem(name).ShouldContain(name, Case.Sensitive);
        }
    }

    [Fact]
    public void EveryBrokenValue_IsReportedTogether()
    {
        var options = new RenderOptions
        {
            PollSeconds = 0,
            MaxMinutes = 0,
            QueueAlertMinutes = 0,
            PollBatchSize = 0,
            PollParallelism = 0,
        };

        RenderOptionsValidator.Validate(options).Count.ShouldBe(5);
    }

    [Fact]
    public void NullOptions_IsRejected()
    {
        Should.Throw<ArgumentNullException>(() => RenderOptionsValidator.Validate(null!));
    }
}
