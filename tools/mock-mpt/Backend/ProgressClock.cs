using NightTales.RenderNode.Contract;

namespace NightTales.MockMpt.Backend;

/// <summary>
/// The mock's progress clock (05 D4 / 11.1.2-T1 Design): elapsed ÷ <c>MOCK_RENDER_SECONDS</c> mapped to the
/// stage boundaries <c>voice</c> 5–29, <c>subtitles</c> 30–39, <c>images</c> 40–49, <c>assembling</c> 50–99.
/// <c>percent</c> is capped at 99 while the task runs and reaches 100 only at the terminal tick (the
/// <c>checking</c> stage and the terminal state), so it never decreases and never claims completion early.
/// </summary>
public static class ProgressClock
{
    private const int FirstPercent = 5;
    private const int NonTerminalPercentCap = 99;
    private const int SubtitlesAt = 30;
    private const int ImagesAt = 40;
    private const int AssemblingAt = 50;

    /// <summary>MPT's <c>state</c> while a task is processing — the code the D14 running fixtures carry.</summary>
    private const int MptRunningState = 4;

    /// <summary>MPT's <c>state</c> once it finished — the code the D14 <c>checking</c>/terminal fixtures carry.</summary>
    private const int MptCompleteState = 1;

    /// <summary>
    /// The raw MPT observables a status carries (05 D4): the mock's own state code and progress. The fixtures
    /// pin <c>4</c> for every running stage and <c>1</c> from <c>checking</c> on, and the mock mirrors them so
    /// a consumer that reads <c>mpt</c> sees the same shape a real node reports.
    /// </summary>
    public static MptStateV1 Snapshot(string stage, int percent)
    {
        ArgumentNullException.ThrowIfNull(stage);

        var complete = string.Equals(stage, RenderStages.Checking, StringComparison.Ordinal)
            || string.Equals(stage, RenderStages.Done, StringComparison.Ordinal);
        return new MptStateV1(complete ? MptCompleteState : MptRunningState, percent, FailedStage: null, Warnings: []);
    }

    /// <summary>The state, stage and percent at <paramref name="elapsed"/> of <paramref name="total"/>.</summary>
    public static (string State, string Stage, int Percent) At(TimeSpan elapsed, TimeSpan total)
    {
        if (elapsed <= TimeSpan.Zero || total <= TimeSpan.Zero)
        {
            return (RenderStates.Queued, RenderStages.Queued, 0);
        }

        var fraction = Math.Clamp(elapsed.TotalSeconds / total.TotalSeconds, 0, 1);
        var percent = Math.Clamp((int)Math.Floor(fraction * 100), FirstPercent, NonTerminalPercentCap);
        var stage = percent switch
        {
            < SubtitlesAt => RenderStages.Voice,
            < ImagesAt => RenderStages.Subtitles,
            < AssemblingAt => RenderStages.Images,
            _ => RenderStages.Assembling,
        };

        return (RenderStates.Running, stage, percent);
    }
}
