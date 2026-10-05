using System.Globalization;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Stories;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Catalog;
using NightTales.Domain.Stories.Keywords;

namespace NightTales.Application.Features.Stories.Render;

/// <summary>Projects a caller-selected approved version without invoking a text or moderation provider.</summary>
/// <param name="db">The current owner's database scope; all three roots retain their tenant filters.</param>
public sealed class StoryRenderInputProvider(IAppDbContext db) : IStoryRenderInputProvider
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<StoryRenderInput>> GetAsync(Guid storyVersionId, CancellationToken ct)
    {
        // One statement reads the immutable version and its episode/series metadata from one committed snapshot.
        // Approval-pointer resolution belongs to the caller; never substitute the episode's current version.
        var row = await (
            from version in db.StoryVersions.AsNoTracking()
            join episode in db.Episodes.AsNoTracking() on version.EpisodeId equals episode.Id
            join series in db.Series.AsNoTracking() on episode.SeriesId equals series.Id
            where version.Id == storyVersionId
            select new
            {
                version.Id,
                version.Body,
                version.Keywords,
                version.Title,
                version.Content,
                series.Language,
                Tier = episode.LengthTier,
            }).SingleOrDefaultAsync(ct);

        if (row is null)
        {
            return Errors.NotFound();
        }

        if (!StoryContentJson.TryReadStored(row.Content, out var content))
        {
            throw new InvalidOperationException("The persisted story version has invalid story content.");
        }

        var segments = new SegmentKeywords[content.Paragraphs.Count + 2];
        segments[0] = new SegmentKeywords("hook", content.Hook.Keywords);
        for (var index = 0; index < content.Paragraphs.Count; index++)
        {
            segments[index + 1] = new SegmentKeywords(
                "p:" + index.ToString(CultureInfo.InvariantCulture),
                content.Paragraphs[index].Keywords);
        }

        segments[^1] = new SegmentKeywords("ending", content.Ending.Keywords);

        return new StoryRenderInput(
            row.Id,
            row.Body,
            row.Keywords,
            row.Title,
            CatalogWireNames.Spelling(row.Language),
            row.Tier,
            segments);
    }
}
