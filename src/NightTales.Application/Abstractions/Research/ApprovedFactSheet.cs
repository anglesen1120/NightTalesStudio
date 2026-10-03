namespace NightTales.Application.Abstractions.Research;

/// <summary>
/// The approved fact sheet of an episode as the writer sees it (02 README D21, CR-24). Declared by 02.2.2-T2;
/// 02.5.5-T1 owns the implementation of <see cref="IFactSheetGuard"/>.
/// </summary>
/// <param name="FactSheetId">The approved sheet's id; null when the episode has none (a non-true-crime episode).</param>
/// <param name="Version">The sheet's version, or null with no sheet.</param>
/// <param name="Facts">The approved facts, source aliases only.</param>
/// <param name="AliasToSourceId">The alias (<c>s1</c>…) to source id map that resolves a fact's sources server-side.</param>
public sealed record ApprovedFactSheet(
    Guid? FactSheetId,
    int? Version,
    IReadOnlyList<FactForWriter> Facts,
    IReadOnlyDictionary<string, Guid> AliasToSourceId)
{
    /// <summary>No sheet: no facts, no sources.</summary>
    public static readonly ApprovedFactSheet None = new(
        null,
        null,
        [],
        new Dictionary<string, Guid>(StringComparer.Ordinal));
}
