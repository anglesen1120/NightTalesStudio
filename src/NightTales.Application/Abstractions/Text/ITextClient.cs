namespace NightTales.Application.Abstractions.Text;

/// <summary>
/// The one port of every structured-output text call (02 README D7/D21). Provider outcomes come back as
/// data (<see cref="TextCallResult{T}"/>), never as an exception: a job maps the case, it does not catch.
/// </summary>
public interface ITextClient
{
    /// <summary>Makes one call and maps its outcome.</summary>
    /// <typeparam name="T">The output document type; deserialised from snake_case JSON.</typeparam>
    /// <param name="req">The call.</param>
    /// <param name="ct">Cancels the call; the only exception a caller can see is its own cancellation.</param>
    /// <returns>The mapped outcome, carrying the usage whenever the provider reported it (D14).</returns>
    ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct)
        where T : class;
}
