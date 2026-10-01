using FluentValidation;

namespace NightTales.Application.Features.Catalog.Queries.ListStyleTemplates;

/// <summary>
/// Shape validation of <c>GET /api/v1/style-templates</c> (09.4.2 plan §Design), run by the D4
/// <c>ValidationBehavior</c> before the handler: <c>?topic=</c> must be the snake_case name of a known
/// topic, and the failure is reported under the wire field <c>topic</c> (09 README D14).
/// </summary>
/// <remarks>
/// An absent filter is legal — the style grid asks for a topic, but a client that lists the catalog
/// before choosing a series gets every active template. An <em>empty</em> or unknown one is a 400, not a
/// silent "no filter" (09.4.2 plan §Failure handling).
/// </remarks>
public sealed class ListStyleTemplatesValidator : AbstractValidator<ListStyleTemplatesQuery>
{
    public ListStyleTemplatesValidator()
    {
        When(
            query => query.Topic is not null,
            () => RuleFor(query => query.Topic)
                .Must(value => CatalogWireNames.TryParseTopic(value, out _))
                .WithMessage("topic_unknown"));
    }
}
