using ErrorOr;
using Mediator;
using NightTales.Application.Features.Catalog.Dtos;

namespace NightTales.Application.Features.Catalog.Queries.ListStyleTemplates;

/// <summary>
/// <c>GET /api/v1/style-templates?topic=</c> (09.4.2 plan §4 UC2, §5): the active style templates a
/// series of that topic may use — the whole closed set, so there is no cursor and no limit
/// ([DECISION] plan §5).
/// </summary>
/// <remarks>
/// The filter is the raw wire string for the same reason as <c>?language=</c>: an unknown topic has to
/// answer 400 <c>validation_failed</c> under field <c>topic</c> (09 README D14), and an omitted topic
/// means "no topic filter" — every active template — not "fiction only" (plan §4 UC2).
/// </remarks>
/// <param name="Topic">The <c>ghost</c>/<c>spiritual</c>/<c>true_crime</c> filter, or <see langword="null"/> for every topic.</param>
public sealed record ListStyleTemplatesQuery(string? Topic)
    : IQuery<ErrorOr<IReadOnlyList<StyleTemplateListItemDto>>>;
