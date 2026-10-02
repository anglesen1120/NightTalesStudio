namespace NightTales.MptAdapter;

/// <summary>
/// The projection of MPT's <c>warnings</c> task-dict member onto the stored snapshot (SEC-11.3.5-T2-03).
/// </summary>
/// <remarks>
/// <c>render-node.v1</c> §4 (ruling §10 T69) freezes the rule: <c>mpt.warnings</c> "is mapped to a closed code
/// set or dropped by the adapter (never the raw MPT observables)". MPT 1.3.7's task dict is
/// <c>extra="allow"</c> and carries no enumerated warning codes (05 C6), and neither the contract nor
/// <c>mpt-api.md</c> defines a code set to map onto, so <see cref="KnownCodes"/> is empty and every string MPT
/// supplies is dropped: free text, node paths and URIs must never reach the stored snapshot, the status body
/// or a log (T69, S7).
/// </remarks>
public static class MptWarnings
{
    /// <summary>
    /// The closed code set <c>render-node.v1</c> §4 allows. Empty today: MPT 1.3.7 emits no enumerated
    /// warning code, so no mapping exists. Adding one to MPT's set is a one-line change here.
    /// </summary>
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal);

    /// <summary>
    /// The warnings to store for a raw MPT list: the members of the closed code set, and nothing else.
    /// An empty or absent member answers an empty list.
    /// </summary>
    public static IReadOnlyList<string> Project(IReadOnlyList<string>? raw)
    {
        if (raw is not { Count: > 0 })
        {
            return [];
        }

        var kept = new List<string>(raw.Count);
        foreach (var warning in raw)
        {
            // A null element is not a string the contract can carry either.
            if (warning is not null && KnownCodes.Contains(warning))
            {
                kept.Add(warning);
            }
        }

        return kept;
    }
}
