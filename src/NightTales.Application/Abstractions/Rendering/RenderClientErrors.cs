using System.Diagnostics.CodeAnalysis;
using ErrorOr;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// Carries a <see cref="RenderClientFailure"/> through the <see cref="ErrorOr.Error"/> an
/// <see cref="IRenderClient"/> or <see cref="IRenderDownloadClient"/> call returns, and reads it back
/// (09 README BD6): the two clients of 09.6.2-T1 and every consumer of 09.6.3 use these two members, so
/// the error shape can never drift between producer and consumer.
/// </summary>
/// <remarks>
/// <para>
/// The failure is stored under <see cref="FailureMetadataKey"/>, which is deliberately <em>not</em> one of
/// the <c>ErrorMetadata</c> keys the Api's writer copies into a ProblemDetails body: this entry is
/// process-local state, never a wire field (09 README D7).
/// </para>
/// <para>
/// <see cref="Error.Code"/> is the failure's outcome code, or the kind's name for a failure that carries
/// none, and <see cref="Error.Description"/> is a fixed sentence only — the D15 S4 sentence of the outcome
/// code where there is one, and a fixed sentence per kind otherwise. No adapter-supplied text is ever
/// copied into either: the raw MPT message never reaches a log, an error body or
/// <c>render_jobs.error_message</c> (05 D4 S4, D8/BD16).
/// </para>
/// </remarks>
public static class RenderClientErrors
{
    /// <summary>The error-metadata key the typed failure travels under.</summary>
    public const string FailureMetadataKey = "render_client_failure";

    /// <summary>The fixed sentence of a <see cref="RenderClientFailureKind.Deferred"/> failure.</summary>
    private const string DeferredDescription =
        "The render node cannot take the job right now; it is offered again later.";

    /// <summary>The fixed sentence of a <see cref="RenderClientFailureKind.TransientFailure"/> failure.</summary>
    private const string TransientDescription =
        "The render node did not answer or failed inside the node; the call may be retried.";

    /// <summary>The fixed sentence of a <see cref="RenderClientFailureKind.OutputNotReady"/> failure.</summary>
    private const string OutputNotReadyDescription =
        "The render output is not downloadable from the node yet; the download is retried.";

    /// <summary>The fixed sentence of a <see cref="RenderClientFailureKind.OutputAbsent"/> failure.</summary>
    private const string OutputAbsentDescription =
        "The node has no such output; nothing is written for it.";

    /// <summary>
    /// Builds the <see cref="Error"/> a client returns for <paramref name="failure"/>: a terminal failure is
    /// an <see cref="ErrorType.Failure"/> (the job ends and the code is recorded), every other kind is an
    /// <see cref="ErrorType.Unexpected"/> (the call is rescheduled or retried instead of recorded).
    /// </summary>
    /// <param name="failure">The mapped failure; its object instance travels in the error's metadata.</param>
    /// <returns>The error to return from the client call.</returns>
    public static Error ToError(RenderClientFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // `Error.Code` mirrors the outcome code; a failure that records no outcome (a deferred or a
        // transient one) is identified by its kind, so a caller can still log a stable code.
        var code = failure.OutcomeCode ?? failure.Kind.ToString();

        var metadata = new Dictionary<string, object>
        {
            [FailureMetadataKey] = failure,
        };

        var description = Describe(failure);

        return failure.Kind == RenderClientFailureKind.TerminalFailure
            ? Error.Failure(code, description, metadata)
            : Error.Unexpected(code, description, metadata);
    }

    /// <summary>
    /// Reads the typed failure back out of <paramref name="error"/>. An error produced anywhere else (an API
    /// error, an <see cref="ErrorType.Validation"/> from a validator) answers <c>false</c>, so a caller can
    /// fall back to its own handling instead of guessing.
    /// </summary>
    /// <param name="error">The error a client call returned.</param>
    /// <param name="failure">The failure it carries, when this method returns <c>true</c>.</param>
    /// <returns><c>true</c> when <paramref name="error"/> carries a render-client failure.</returns>
    public static bool TryGetFailure(Error error, [NotNullWhen(true)] out RenderClientFailure? failure)
    {
        failure = null;

        if (error.Metadata is null || !error.Metadata.TryGetValue(FailureMetadataKey, out var value))
        {
            return false;
        }

        if (value is not RenderClientFailure typed)
        {
            return false;
        }

        failure = typed;
        return true;
    }

    /// <summary>
    /// The fixed sentence for a failure: the D15 S4 sentence of its outcome code when it has one, otherwise the
    /// sentence of its kind. <see cref="OutcomeMessages.For"/> supplies the fallback for a code that is not one
    /// of the ten, so this never throws and never echoes input.
    /// </summary>
    private static string Describe(RenderClientFailure failure) => failure.Kind switch
    {
        RenderClientFailureKind.Deferred => DeferredDescription,
        RenderClientFailureKind.TransientFailure => TransientDescription,
        RenderClientFailureKind.OutputNotReady => OutputNotReadyDescription,
        RenderClientFailureKind.OutputAbsent => OutputAbsentDescription,
        _ => failure.OutcomeCode is { Length: > 0 } outcome
            ? OutcomeMessages.For(outcome)
            : OutcomeMessages.For(RenderOutcomeCodes.RenderNodeError),
    };
}
