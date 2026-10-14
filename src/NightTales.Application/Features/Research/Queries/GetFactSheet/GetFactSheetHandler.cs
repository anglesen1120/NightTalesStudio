using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Queries.GetFactSheet;

/// <summary>
/// Reads the episode's current fact-sheet version (02.5.4-T1, plan §4.4): the row with the highest version,
/// whatever its status, so a client always reads what the next edit will replace. The tenant filter on both
/// queryables means a foreign, unknown or sheet-less episode answers the same 404 <c>fact_sheet_not_found</c>.
/// </summary>
/// <remarks>
/// The read model is read-only and untracked, so a source deleted since the version was saved cannot make this
/// read fail: the reference is rendered with an empty title and no URL (<see cref="FactSheetWire.MissingSourceTitle"/>),
/// which is what the Superseded/draft view needs to show a fact that can no longer be backed.
/// </remarks>
/// <param name="readDb">The read model.</param>
public sealed class GetFactSheetHandler(IReadDb readDb) : IQueryHandler<GetFactSheetQuery, ErrorOr<FactSheetDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<FactSheetDto>> Handle(
        GetFactSheetQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sheet = await readDb.Query<FactSheet>()
            .Where(candidate => candidate.EpisodeId == query.EpisodeId)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (sheet is null)
        {
            return Error.NotFound(ErrorCodes.FactSheetNotFound, "No fact sheet exists for this episode.");
        }

        var sources = await readDb.Query<StorySource>()
            .Where(source => source.EpisodeId == query.EpisodeId)
            .Select(source => new { source.Id, source.Kind, source.Title, source.Url, source.FinalUrl })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = sources.ToDictionary(source => source.Id);

        var aliasBySourceId = new Dictionary<Guid, string>(sheet.SourceAliases.Count);
        foreach (var pair in sheet.SourceAliases)
        {
            aliasBySourceId[pair.Value] = pair.Key;
        }

        var facts = new List<FactItemDto>(sheet.Facts.Count);
        foreach (var fact in sheet.Facts)
        {
            var refs = new List<FactSourceRefDto>(fact.SourceIds.Count);
            foreach (var sourceId in fact.SourceIds)
            {
                var alias = aliasBySourceId.TryGetValue(sourceId, out var known) ? known : string.Empty;

                if (!byId.TryGetValue(sourceId, out var source))
                {
                    refs.Add(new FactSourceRefDto(sourceId, alias, FactSheetWire.MissingSourceTitle, null, false));
                    continue;
                }

                if (source.Kind == StorySourceKind.UserNote)
                {
                    refs.Add(new FactSourceRefDto(sourceId, alias, FactSheetWire.UserNoteTitle, null, true));
                    continue;
                }

                var title = source.Title ?? source.FinalUrl ?? source.Url ?? FactSheetWire.MissingSourceTitle;
                refs.Add(new FactSourceRefDto(sourceId, alias, title, source.Url, false));
            }

            facts.Add(new FactItemDto(
                fact.Id,
                fact.Statement,
                refs,
                fact.SourceOverlap,
                FactSheetWire.Origin(fact.Origin),
                FactSheetWire.Confidence(fact.Confidence)));
        }

        return new FactSheetDto(
            sheet.Id,
            sheet.Version,
            FactSheetWire.Status(sheet.Status),
            ETags.FactSheet(sheet.Id),
            facts,
            sheet.PromptVersion,
            sheet.CreatedAt,
            sheet.ApprovedAt,
            sheet.MaxFactId);
    }
}
