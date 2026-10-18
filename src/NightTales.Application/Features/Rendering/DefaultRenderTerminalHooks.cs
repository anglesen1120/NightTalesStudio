using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The default <see cref="IRenderTerminalHooks"/> of 09.6.3-T1: it records nothing and classifies an outcome
/// with the adapter's own D5 code.
/// </summary>
/// <remarks>
/// It exists so the poller's terminal order is wired end to end before 03.4.2-T1 lands, and it is registered
/// with <c>TryAdd</c> so that task's real recorder and classifier replace it without touching the poller.
/// Nothing here writes a row, so the default is a singleton and needs no scope.
/// </remarks>
public sealed class DefaultRenderTerminalHooks : IRenderTerminalHooks
{
    /// <summary>
    /// The D5 code a failed status without a catalogued outcome is classified with (05 D5's generic node
    /// failure; 03 D7's never-retried list does not cover it, so the retry policy decides).
    /// </summary>
    public const string NodeErrorCode = "render_node_error";

    /// <inheritdoc />
    /// <remarks>The default records nothing: image usage is 03.4.2-T1's write path, not this task's.</remarks>
    public ValueTask RecordImageUsageAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(status);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The adapter's own outcome code when it is one of the ten of <see cref="RenderJob.OutcomeCodes"/>, and
    /// <see cref="NodeErrorCode"/> otherwise (a missing or uncatalogued outcome). The per-outcome retryable
    /// flag is left <see langword="null"/>: only 03.4.2-T1's classifier knows it.
    /// </remarks>
    public ValueTask<RenderFailureClassification> ClassifyAsync(
        RenderJob job,
        RenderStatusV1 status,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(status);

        var reported = status.Outcome?.Code;
        var code = reported is not null && RenderJob.OutcomeCodes.Contains(reported, StringComparer.Ordinal)
            ? reported
            : NodeErrorCode;

        return ValueTask.FromResult(new RenderFailureClassification(code, null));
    }
}
