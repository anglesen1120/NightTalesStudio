using NightTales.Application.Abstractions.Research;

namespace NightTales.Application.Features.Stories.Facts;

/// <summary>
/// The outcome of <see cref="FactSourcesBuilder.Build"/> (02.5.5-T1, D2): the <c>fact_sources</c> the stored story
/// carries, the draft's fact refs the approved sheet cannot resolve, and the cited source ids whose row is gone.
/// Neither list fails the run; the caller reports counts only (D19).
/// </summary>
/// <param name="Sources">The built refs, de-duplicated by source id and ordered by first use.</param>
/// <param name="UnknownRefs">The draft's fact refs absent from the approved sheet, in first-use order and de-duplicated.</param>
/// <param name="SkippedSourceIds">
/// The cited source ids with no <c>story_sources</c> row of this episode and owner, in first-use order and
/// de-duplicated: a source deleted after approval is skipped, never an error (02.5.5-T1 failure handling).
/// </param>
public sealed record FactSourcesResult(
    IReadOnlyList<FactSourceRef> Sources,
    IReadOnlyList<string> UnknownRefs,
    IReadOnlyList<Guid> SkippedSourceIds);
