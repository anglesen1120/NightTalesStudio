using System.Globalization;
using ErrorOr;

namespace NightTales.Domain.Research;

/// <summary>
/// Assigns the fact ids of one edited fact-sheet version (02.5.4-T1, README F12): a fact the user kept keeps
/// the id it already had, a fact the user added gets the id after the episode's watermark, and an id is never
/// handed out twice — not even after the fact that carried it was deleted, because the watermark only ever
/// grows (plan §3 invariant 2). Pure and deterministic: the current version's ids and watermark come in as
/// arguments and the input order decides the new ids, so the same body always allocates the same ids.
/// </summary>
public static class FactIdAllocator
{
    /// <summary>
    /// The catalogued code of a fact-sheet body that fails validation (400, 02 README D8/T51, ruling T51). The
    /// Domain may not reference <c>NightTales.Application.Common.Errors.ErrorCodes</c> (ErrorOr is its only
    /// package), so it spells the code itself; the unit test
    /// <c>Allocate_InvalidContentCode_MatchesTheErrorCatalogue</c> keeps the two equal.
    /// </summary>
    public const string InvalidContentCode = "fact_sheet_invalid_content";

    /// <summary>The metadata key under which the failing body field travels to the client (02 README D7).</summary>
    public const string FieldMetadataKey = "field";

    /// <summary>
    /// Allocates the ids of one version: a non-null input id that names a fact of the current version is kept
    /// verbatim and in the given order, a null input is a new fact and gets <c>f{watermark + 1}</c>.
    /// </summary>
    /// <param name="currentIds">The ids of the facts in the episode's current version.</param>
    /// <param name="maxFactId">
    /// The current version's <c>max_fact_id</c>: at least the highest id in <paramref name="currentIds"/>, and
    /// possibly higher, because a deleted fact retires its id (F12).
    /// </param>
    /// <param name="requestedIds">The input fact ids in input order; null is a new fact.</param>
    /// <returns>The ids in input order, and the new watermark (<paramref name="maxFactId"/> or higher).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxFactId"/> is negative, or below the highest id in <paramref name="currentIds"/> — a
    /// caller bug, because the watermark is monotonic (plan §3 invariant 2).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An id in <paramref name="currentIds"/> is not a backend fact id (<c>f</c> plus a 1-based number).
    /// </exception>
    public static ErrorOr<FactIdAllocation> Allocate(
        IReadOnlyCollection<string> currentIds,
        int maxFactId,
        IReadOnlyList<string?> requestedIds)
    {
        ArgumentNullException.ThrowIfNull(currentIds);
        ArgumentNullException.ThrowIfNull(requestedIds);

        if (maxFactId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFactId), maxFactId, "max_fact_id cannot be negative.");
        }

        var current = new HashSet<string>(currentIds, StringComparer.Ordinal);
        EnsureWatermarkCoversTheCurrentVersion(current, maxFactId);

        var ids = new List<string>(requestedIds.Count);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var nextFactId = maxFactId;

        for (var index = 0; index < requestedIds.Count; index++)
        {
            var requested = requestedIds[index];
            if (requested is null)
            {
                nextFactId++;
                ids.Add(Format(nextFactId));
                continue;
            }

            // A body may only keep an id the current version carries, and only once: anything else cannot be
            // stored, so it is the client's bug and the answer is the catalogue's 400 with the field named.
            if (!current.Contains(requested) || !kept.Add(requested))
            {
                return Error.Validation(
                    InvalidContentCode,
                    "Every fact id must name a fact of the current version, at most once.",
                    new Dictionary<string, object> { [FieldMetadataKey] = $"facts[{index}].id" });
            }

            ids.Add(requested);
        }

        return new FactIdAllocation(ids, nextFactId);
    }

    /// <summary>Formats one backend fact id (<c>f</c> plus its 1-based number).</summary>
    /// <param name="number">The 1-based id number.</param>
    /// <returns>The stored/wire spelling.</returns>
    internal static string Format(int number) => string.Concat("f", number.ToString(CultureInfo.InvariantCulture));

    /// <summary>Parses a backend fact id (<c>f</c> plus a 1-based number, no leading zero).</summary>
    /// <param name="factId">The candidate id.</param>
    /// <param name="number">The parsed number, or 0.</param>
    /// <returns>Whether <paramref name="factId"/> is a backend fact id.</returns>
    internal static bool TryParseNumber(string? factId, out int number)
    {
        number = 0;
        return factId is { Length: >= 2 }
            && factId[0] == 'f'
            && factId[1] != '0'
            && int.TryParse(factId.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number >= 1;
    }

    /// <summary>
    /// Fails a caller that would hand out an id a kept fact already carries: the watermark is the episode's,
    /// not the current version's, so it can only be equal to or above the highest id the version shows.
    /// </summary>
    private static void EnsureWatermarkCoversTheCurrentVersion(IReadOnlyCollection<string> currentIds, int maxFactId)
    {
        foreach (var id in currentIds)
        {
            if (!TryParseNumber(id, out var number))
            {
                throw new ArgumentException(
                    $"Fact id '{id}' must be 'f' followed by its 1-based number.",
                    nameof(currentIds));
            }

            if (number > maxFactId)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxFactId),
                    maxFactId,
                    $"max_fact_id must be at least the highest current fact id ({number}).");
            }
        }
    }
}

/// <summary>The fact ids of one edited version, in input order (02.5.4-T1, F12).</summary>
/// <param name="Ids">The ids, one per input fact, in the same order as the request body.</param>
/// <param name="MaxFactId">The version's watermark: never lower than the watermark it was allocated from.</param>
public sealed record FactIdAllocation(IReadOnlyList<string> Ids, int MaxFactId);
