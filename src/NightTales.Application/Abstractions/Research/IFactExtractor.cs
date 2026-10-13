using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Research.Facts;

namespace NightTales.Application.Abstractions.Research;

/// <summary>
/// The fact-extraction step of the true-crime path (02 README D21, 02.5.3-T1): one structured-output call
/// turns the episode's fetched source texts into schema-valid <see cref="FactsV1"/> — statements plus
/// source aliases — inside a bounded input budget.
/// </summary>
/// <remarks>
/// <para>
/// The model never emits a fact id or a URL: the backend assigns ids (<c>f1…</c>) and maps aliases
/// (<c>s1…s10</c>) to <c>story_sources.id</c> (D6/F12, CR-21). <see cref="FactSourceInput.SourceId"/> is
/// backend-only and is never written into the call's data object.
/// </para>
/// <para>
/// Provider outcomes come back as data (<see cref="TextCallResult{T}"/>) and are returned untouched: the
/// fact-sheet job (02.5.3) maps <c>InvalidOutput</c>, <c>Moderated</c> and the error cases to run error
/// codes (D7). The only exception a caller sees is its own cancellation.
/// </para>
/// </remarks>
public interface IFactExtractor
{
    /// <summary>Makes one fact-extraction call and maps its outcome.</summary>
    /// <param name="req">The request; its sources are a <c>FactInputBudget</c> plan (alias-assigned, truncated, sanitised).</param>
    /// <param name="ct">Cancels the call; the only exception a caller can see is its own cancellation.</param>
    /// <returns>The mapped outcome; the parsed <see cref="FactsV1"/> on success.</returns>
    ValueTask<TextCallResult<FactsV1>> ExtractAsync(FactExtractionRequest req, CancellationToken ct);
}
