using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Catalog.Dtos;
using NightTales.Domain.Catalog;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Catalog.Queries.ListStyleTemplates;

/// <summary>
/// <c>GET /api/v1/style-templates</c> (09.4.2 plan §4 UC2, §5): the active style templates, filtered by
/// the shared true-crime rule and projected in the order the route's ETag is computed over.
/// </summary>
/// <remarks>
/// <para>
/// One query, no tracking and no <c>Include</c> (D8/Q1). The topic rule is applied <em>after</em> the
/// read, through <see cref="StyleTopicPolicy.IsAllowed"/>: the policy resolves topic × hint in code, and
/// re-expressing it as SQL would duplicate the rule 03 owns (03 CR-2, forbidden by reuse). The candidate
/// set is the closed seven-template catalog (03 D2), so this is a handful of rows and not a table scan.
/// </para>
/// <para>
/// A de-activated template is hidden here but stays usable by a series that already references it
/// (03 README L98) — the catalog list never rewrites history.
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model (D8).</param>
/// <param name="publicAssetUrl">Composes the public preview URL from the stored key.</param>
public sealed class ListStyleTemplatesHandler(IReadDb readDb, IPublicAssetUrl publicAssetUrl)
    : IQueryHandler<ListStyleTemplatesQuery, ErrorOr<IReadOnlyList<StyleTemplateListItemDto>>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<IReadOnlyList<StyleTemplateListItemDto>>> Handle(
        ListStyleTemplatesQuery query,
        CancellationToken cancellationToken)
    {
        Topic? topic = null;
        if (query.Topic is not null)
        {
            // The validator of this folder already refused an unknown value on the route; repeating the
            // check keeps a handler that is called without the pipeline from filtering by garbage.
            if (!CatalogWireNames.TryParseTopic(query.Topic, out var parsed))
            {
                return Errors.ValidationFailed(
                    "topic",
                    "The topic filter is not a known topic (ghost, spiritual, true_crime).");
            }

            topic = parsed;
        }

        var rows = await readDb.Query<StyleTemplate>()
            .Where(template => template.Active)
            .OrderBy(template => template.Id)
            .Select(template => new Row(
                template.Id,
                template.Name,
                template.TopicHint,
                template.PreviewStorageKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Where(row => topic is null || StyleTopicPolicy.IsAllowed(topic.Value, row.TopicHint))
            .Select(row => new StyleTemplateListItemDto(
                row.Id,
                row.Name,
                CatalogWireNames.Spelling(row.TopicHint),
                publicAssetUrl.Build(row.PreviewStorageKey)?.AbsoluteUri))
            .ToList();
    }

    /// <summary>The projected catalog row, before the policy, the URL and the wire spelling are applied.</summary>
    private sealed record Row(
        Guid Id,
        string Name,
        Topic? TopicHint,
        string? PreviewStorageKey);
}
