using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Research;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Research;

/// <summary>
/// The real <see cref="IFactSheetGuard"/> (02 README D21/CR-24, 02.5.5-T1): a fiction episode needs no sheet
/// and pays no read at all, while a true-crime episode may only run on the episode's currently approved,
/// non-empty sheet.
/// </summary>
/// <remarks>
/// <para>
/// Both reads go through the scoped <see cref="IReadDb"/>, so the D10 tenant filter restricts them to the
/// caller's own rows: another user's sheet is invisible here and therefore answers
/// <c>fact_sheet_required</c>, exactly like a missing sheet — and the Worker's scope pins the same user
/// (<c>ScopedCurrentUser.For</c>) before the pipeline resolves this type, so a job reads what its user's
/// request would have read. The guard is read-only and never widens a scope: it has no
/// <c>IgnoreQueryFilters</c> and cannot read across tenants.
/// </para>
/// <para>
/// Fail closed, never open: a read failure propagates to the caller (no catch-all turns it into
/// <see cref="ApprovedFactSheet.None"/>), and a sheet whose facts cite an alias its stored alias map does not
/// carry, or whose map names a source row of another episode or another owner, is refused with the same
/// <c>fact_sheet_required</c> as a missing sheet. A cited source that no longer exists is deliberately not an
/// error (its row was deleted after approval): it is indistinguishable from a row the tenant filter hides, and
/// the fact-source builder skips it and reports it instead (02.5.5-T1, F26).
/// </para>
/// <para>
/// The returned facts carry the fact id, the statement and the cited aliases only — never a source id, a URL,
/// a title, a confidence or an overlap (D6/F12) — and the alias map is the one persisted on the approved row
/// (CR-4/D21), never one re-derived from the live <c>story_sources</c>: editing a source therefore cannot
/// corrupt the citation a stored fact carries.
/// </para>
/// </remarks>
/// <param name="readDb">The read model the approved row and the alias map's source rows are read from.</param>
public sealed class FactSheetGuard(IReadDb readDb) : IFactSheetGuard
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<ApprovedFactSheet>> EnsureApprovedAsync(
        Guid episodeId,
        Topic topic,
        CancellationToken ct)
    {
        // Fiction pays nothing: 02 README D16's topics need no sheet, so this path never opens the read model.
        if (topic.IsFiction())
        {
            return ApprovedFactSheet.None;
        }

        // At most one approved row per episode is enforced by ux_fact_sheets_episode_approved; the ordering is
        // the defensive tie-break of the read routes, so a legacy row can never hand over an older version.
        var sheet = await readDb.Query<FactSheet>()
            .Where(candidate => candidate.EpisodeId == episodeId && candidate.Status == FactSheetStatus.Approved)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // A draft or superseded version never qualifies, and an approved version without facts is unusable.
        if (sheet is null || sheet.Facts.Count == 0)
        {
            return Required();
        }

        // Every alias a fact cites must be a key of the stored map, or the server cannot resolve the citation.
        foreach (var fact in sheet.Facts)
        {
            foreach (var alias in fact.SourceAliases)
            {
                if (!sheet.SourceAliases.ContainsKey(alias))
                {
                    return Inconsistent();
                }
            }
        }

        // S2/F4: the fact ids of one sheet are distinct. The write path (FactSheet.CreateVersion) rejects a
        // repeat, so only a corrupt row can get here, and the guard is the fail-closed read side: the writer's
        // fact references and the story page's fact list are keyed by id, so a repeat would silently make the
        // last entry win.
        var factIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in sheet.Facts)
        {
            if (!factIds.Add(fact.Id))
            {
                return Inconsistent();
            }
        }

        // Every id the stored map names must be a source of this episode and this owner, when its row is read:
        // a visible row of another episode or owner is a corrupt sheet, while an absent row is a deleted source
        // (accepted above). The projection reads no text, no URL and no key of the source row.
        if (sheet.SourceAliases.Count > 0)
        {
            var sourceIds = sheet.SourceAliases.Values.Distinct().ToArray();
            var rows = await readDb.Query<StorySource>()
                .Where(source => sourceIds.Contains(source.Id))
                .Select(source => new { source.EpisodeId, source.UserId })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                if (row.EpisodeId != episodeId || row.UserId != sheet.UserId)
                {
                    return Inconsistent();
                }
            }
        }

        var facts = new FactForWriter[sheet.Facts.Count];
        for (var index = 0; index < sheet.Facts.Count; index++)
        {
            var fact = sheet.Facts[index];
            facts[index] = new FactForWriter(fact.Id, fact.Statement, fact.SourceAliases);
        }

        return new ApprovedFactSheet(sheet.Id, sheet.Version, facts, sheet.SourceAliases);
    }

    /// <summary>The D8 409 of a true-crime episode that has no usable approved sheet (02 README D8).</summary>
    private static Error Required() => Error.Conflict(
        ErrorCodes.FactSheetRequired,
        "A true-crime story needs an approved fact sheet.");

    /// <summary>
    /// The same D8 409 for an approved sheet whose own documents disagree: a citation the stored map cannot
    /// resolve would reach the writer as an alias nothing can translate, and a repeated fact id would make the
    /// last entry of that id silently win, because both the writer's references and the story page key facts by id.
    /// </summary>
    private static Error Inconsistent() => Error.Conflict(
        ErrorCodes.FactSheetRequired,
        "The episode's approved fact sheet is inconsistent: a citation the sheet cannot resolve, or a repeated fact id.");
}
