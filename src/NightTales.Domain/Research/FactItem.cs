namespace NightTales.Domain.Research;

/// <summary>
/// One stored fact of a fact sheet (02.5.3 plan §3, stored inside the <c>facts jsonb</c> column — it has no
/// table of its own). Ids are backend-assigned <c>f1…fN</c> and never reused across an episode's versions;
/// the cited sources are resolved from the model's aliases server-side, so the model never produces a source
/// id or a URL (D6/F12).
/// </summary>
/// <param name="Id">The backend-assigned id, <c>f</c> plus a 1-based number, unique within the episode.</param>
/// <param name="Statement">The fact, NFC-normalised with collapsed whitespace; never a copied source sentence.</param>
/// <param name="SourceIds">The cited <c>story_sources</c> ids, at least one, in the order of <paramref name="SourceAliases"/>.</param>
/// <param name="SourceAliases">The aliases the fact cited (<c>s1…s10</c>), kept for the display order (CR-4).</param>
/// <param name="SourceOverlap">The 02.5.6 <c>ScoreFact</c> result of the statement against its cited sources; null when unscored.</param>
/// <param name="Confidence">How well the cited sources support the statement (T53); copied through unchanged.</param>
/// <param name="Origin">Whether the model or a user produced the statement.</param>
public sealed record FactItem(
    string Id,
    string Statement,
    IReadOnlyList<Guid> SourceIds,
    IReadOnlyList<string> SourceAliases,
    decimal? SourceOverlap,
    FactConfidence Confidence,
    FactOrigin Origin);
