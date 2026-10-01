namespace NightTales.Application.Features.Catalog.Dtos;

/// <summary>
/// One row of <c>GET /api/v1/style-templates</c> (09.4.2 plan §5, 03 D2): the members the 10.4.2 style
/// grid needs, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Id"/> is the template's surrogate key, which is what a series stores as its
/// <c>styleTemplateId</c>; <see cref="Name"/> is the natural key the 03 catalog seeds by, and the
/// label the UI shows comes from i18n (<c>series.style.&lt;name&gt;.label</c>/<c>.hint</c>, 03 CR-7),
/// never from the row's template text.
/// </para>
/// <para>
/// <see cref="TopicHint"/> is the snake_case topic the template is meant for, or <see langword="null"/>
/// when it is usable for any fiction topic; the true-crime rule itself is applied by the query through
/// <c>StyleTopicPolicy</c> (03 CR-2), so a client never has to re-implement it.
/// </para>
/// </remarks>
/// <param name="Id">The template's surrogate key.</param>
/// <param name="Name">The template's natural key.</param>
/// <param name="TopicHint">The topic the template is intended for, snake_case, or <see langword="null"/>.</param>
/// <param name="PreviewUrl">The public preview URL, or <see langword="null"/>.</param>
public sealed record StyleTemplateListItemDto(
    Guid Id,
    string Name,
    string? TopicHint,
    string? PreviewUrl);
