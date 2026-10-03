namespace NightTales.Application.Abstractions.Text;

/// <summary>
/// The ambient correlation of the call in flight (02 README D7): the SDK's request options carry no
/// headers, so the HTTP layer reads this scope to set <c>X-Client-Request-Id</c> — and to mark the request
/// as dispatched at the exact moment the transport sends it (02.2.1-T2 review).
/// </summary>
public static class TextCallScope
{
    private static readonly AsyncLocal<CallScope?> Slot = new();

    /// <summary>The current <c>&lt;generation_run_id&gt;:&lt;step&gt;:&lt;attempt&gt;</c>, or <see langword="null"/> outside a call.</summary>
    public static string? Current => Slot.Value?.Id;

    /// <summary>
    /// Records that the call's request may now be in flight (or already billed): the transport handler calls it
    /// just before the send. No-op outside a call scope.
    /// </summary>
    public static void MarkDispatched() => Slot.Value?.RequestDispatched();

    /// <summary>Opens the scope of one call; dispose it to restore the previous value.</summary>
    /// <param name="run">The run of the call.</param>
    /// <param name="step">The step of the call.</param>
    /// <param name="request">The call's request, so the transport marks its dispatch on it; optional for scopes that only need the correlation id.</param>
    /// <param name="model">The model the call is sent to.</param>
    /// <returns>The scope to dispose.</returns>
    public static IDisposable Push(RunContext run, TextStep step, TextCallRequest? request = null, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        var previous = Slot.Value;
        Slot.Value = new CallScope(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{run.GenerationRunId}:{step.ToString().ToLowerInvariant()}:{run.Attempt}"),
            request,
            model);

        return new Restore(previous);
    }

    private sealed record CallScope(string Id, TextCallRequest? Request, string? Model)
    {
        internal void RequestDispatched()
        {
            if (Request is { } request && Model is { } model)
            {
                request.MarkDispatched(model);
            }
        }
    }

    private sealed class Restore(CallScope? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
