using NightTales.Application.Abstractions.Text;

namespace NightTales.Application.Abstractions.Moderation;

/// <summary>
/// The ambient run of the moderation check in flight. <see cref="IContentModerator.CheckAsync"/> (D12) takes no
/// run context, but its <c>usage_records</c> row (D14) needs the owner, episode, reservation and price version:
/// the job pushes its <see cref="RunContext"/> here around the check, exactly as <see cref="TextCallScope"/>
/// carries the correlation id of a text call. Outside a scope the check runs and is not metered.
/// </summary>
public static class ModerationScope
{
    private static readonly AsyncLocal<RunContext?> Slot = new();

    /// <summary>Gets the run of the check in flight, or <see langword="null"/> outside a scope.</summary>
    public static RunContext? Current => Slot.Value;

    /// <summary>Opens the scope of the checks of one run; dispose it to restore the previous value.</summary>
    /// <param name="run">The run the checks belong to.</param>
    /// <returns>The scope to dispose.</returns>
    public static IDisposable Push(RunContext run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var previous = Slot.Value;
        Slot.Value = run;

        return new Restore(previous);
    }

    private sealed class Restore(RunContext? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
