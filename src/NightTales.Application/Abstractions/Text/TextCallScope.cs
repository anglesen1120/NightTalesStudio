namespace NightTales.Application.Abstractions.Text;

/// <summary>
/// The ambient correlation id of the call in flight (02 README D7): the SDK's request options carry no
/// headers, so the HTTP layer reads this scope to set <c>X-Client-Request-Id</c>.
/// </summary>
public static class TextCallScope
{
    private static readonly AsyncLocal<string?> Slot = new();

    /// <summary>The current <c>&lt;generation_run_id&gt;:&lt;step&gt;:&lt;attempt&gt;</c>, or <see langword="null"/> outside a call.</summary>
    public static string? Current => Slot.Value;

    /// <summary>Opens the scope of one call; dispose it to restore the previous value.</summary>
    /// <param name="run">The run of the call.</param>
    /// <param name="step">The step of the call.</param>
    /// <returns>The scope to dispose.</returns>
    public static IDisposable Push(RunContext run, TextStep step)
    {
        ArgumentNullException.ThrowIfNull(run);

        var previous = Slot.Value;
        Slot.Value = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{run.GenerationRunId}:{step.ToString().ToLowerInvariant()}:{run.Attempt}");

        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
