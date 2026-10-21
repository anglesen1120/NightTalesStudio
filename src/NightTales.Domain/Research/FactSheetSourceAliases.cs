using System.Globalization;

namespace NightTales.Domain.Research;

/// <summary>
/// Reconciles the alias→source-id map of one edited fact-sheet version (02.5.4-T1, CR-4/D21): a source a
/// previous version already named keeps its alias, so a citation a stored fact carries never shifts, and a
/// source cited for the first time gets the smallest free <c>s&lt;N&gt;</c>. Pure, deterministic and
/// order-stable: the same previous map and the same citation order always produce the same result.
/// </summary>
public static class FactSheetSourceAliases
{
    /// <summary>
    /// The highest alias number the scheme issues (<c>s1…s10</c>, README F12). The Application layer bounds every
    /// fact-extraction call by the same number (<c>FactInputLimits.MaxSourcesCeiling</c>), which cannot be named
    /// from here: the Domain does not reference the Application layer.
    /// </summary>
    public const int MaxAliasNumber = 10;

    /// <summary>
    /// Builds the version's alias map: every cited source that <paramref name="previous"/> already named keeps its
    /// alias, and each source cited for the first time takes the smallest <c>s&lt;N&gt;</c> (<c>1 ≤ N ≤
    /// <see cref="MaxAliasNumber"/></c>) no still-cited source holds, assigned in first citation order. An alias of
    /// a source this body no longer cites is free again, so the ten aliases the model reads (README F12) cannot run
    /// out while at most <see cref="MaxAliasNumber"/> distinct sources are cited; an alias a still-cited source
    /// carries never moves. An alias whose <c>s&lt;N&gt;</c> shape is unknown neither occupies a number nor is
    /// renumbered, and it is returned only when its source is cited.
    /// </summary>
    /// <param name="previous">The alias map the episode's current version carries; an alias of a source the new facts still cite is kept.</param>
    /// <param name="citedSourceIdsInFirstCitationOrder">The source ids the new facts cite, in the order they are first cited; a repeat is ignored.</param>
    /// <returns>The alias map of the new version, holding exactly the cited sources, in first citation order.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">More than <see cref="MaxAliasNumber"/> distinct sources are cited, which the <c>s1…s10</c> scheme cannot name.</exception>
    public static IReadOnlyDictionary<string, Guid> Reconcile(
        IReadOnlyDictionary<string, Guid> previous,
        IReadOnlyList<Guid> citedSourceIdsInFirstCitationOrder)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(citedSourceIdsInFirstCitationOrder);

        var cited = new List<Guid>(citedSourceIdsInFirstCitationOrder.Count);
        var citedSet = new HashSet<Guid>();
        foreach (var sourceId in citedSourceIdsInFirstCitationOrder)
        {
            if (citedSet.Add(sourceId))
            {
                cited.Add(sourceId);
            }
        }

        // The guard is the scheme's own ceiling (s1..s10), not the request: the caller bounds each fact's
        // citation list; the number of distinct sources of one sheet has to be bounded here, because this
        // method is the only thing that can name them.
        if (cited.Count > MaxAliasNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(citedSourceIdsInFirstCitationOrder),
                cited.Count,
                $"A fact sheet cites at most {MaxAliasNumber} distinct sources (aliases s1..s{MaxAliasNumber}).");
        }

        // A still-cited source keeps the alias the previous version named it under, so a stored citation never
        // shifts. An alias of a source this body dropped is not reserved: with only ten numbers available, the
        // map must be able to reuse it, or an episode could run out of aliases (README F12).
        var aliasOfCitedSource = new Dictionary<Guid, string>();
        foreach (var pair in previous)
        {
            // A source named twice under two aliases keeps the one the map lists first; the map is written by
            // the two paths of this feature, which never produce that shape.
            if (citedSet.Contains(pair.Value))
            {
                aliasOfCitedSource.TryAdd(pair.Value, pair.Key);
            }
        }

        var reservedAliases = new HashSet<string>(aliasOfCitedSource.Values, StringComparer.Ordinal);
        var reconciled = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var nextAliasNumber = 1;

        foreach (var sourceId in cited)
        {
            if (aliasOfCitedSource.TryGetValue(sourceId, out var stable))
            {
                reconciled[stable] = sourceId;
                continue;
            }

            while (reservedAliases.Contains(Alias(nextAliasNumber)))
            {
                nextAliasNumber++;
            }

            var assigned = Alias(nextAliasNumber);
            reservedAliases.Add(assigned);
            aliasOfCitedSource[sourceId] = assigned;
            reconciled[assigned] = sourceId;
            nextAliasNumber++;
        }

        return reconciled;
    }

    /// <summary>Formats one source alias (<c>s</c> plus its 1-based number).</summary>
    private static string Alias(int number) => string.Concat("s", number.ToString(CultureInfo.InvariantCulture));
}
