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
    /// Builds the version's alias map: every cited source keeps the alias <paramref name="previous"/> gave it,
    /// and each new source takes the smallest <c>s&lt;N&gt;</c> (<c>N ≥ 1</c>) that map does not name, assigned
    /// in first citation order. An alias the previous version used is never recycled for another source — not
    /// even when the body no longer cites the source it belonged to — because the alias is the stable half of a
    /// stored citation and two versions must not read alike while meaning different sources.
    /// </summary>
    /// <param name="previous">The alias map the episode's current version carries; an alias it already names is never reassigned.</param>
    /// <param name="citedSourceIdsInFirstCitationOrder">The source ids the new facts cite, in the order they are first cited; a repeat is ignored.</param>
    /// <returns>The alias map of the new version, holding exactly the cited sources, in first citation order.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static IReadOnlyDictionary<string, Guid> Reconcile(
        IReadOnlyDictionary<string, Guid> previous,
        IReadOnlyList<Guid> citedSourceIdsInFirstCitationOrder)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(citedSourceIdsInFirstCitationOrder);

        var aliasBySourceId = new Dictionary<Guid, string>();
        foreach (var pair in previous)
        {
            // A source named twice under two aliases keeps the one the map lists first; the map is written by
            // the two paths of this feature, which never produce that shape.
            aliasBySourceId.TryAdd(pair.Value, pair.Key);
        }

        var usedAliases = new HashSet<string>(previous.Keys, StringComparer.Ordinal);
        var reconciled = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var cited = new HashSet<Guid>();
        var nextAliasNumber = 1;

        foreach (var sourceId in citedSourceIdsInFirstCitationOrder)
        {
            if (!cited.Add(sourceId))
            {
                continue;
            }

            if (aliasBySourceId.TryGetValue(sourceId, out var stable))
            {
                reconciled[stable] = sourceId;
                continue;
            }

            while (usedAliases.Contains(Alias(nextAliasNumber)))
            {
                nextAliasNumber++;
            }

            var assigned = Alias(nextAliasNumber);
            usedAliases.Add(assigned);
            aliasBySourceId[sourceId] = assigned;
            reconciled[assigned] = sourceId;
            nextAliasNumber++;
        }

        return reconciled;
    }

    /// <summary>Formats one source alias (<c>s</c> plus its 1-based number).</summary>
    private static string Alias(int number) => string.Concat("s", number.ToString(CultureInfo.InvariantCulture));
}
