using FluentValidation;

namespace NightTales.Application.Features.Catalog.Queries.ListVoices;

/// <summary>
/// Shape validation of <c>GET /api/v1/voices</c> (09.4.2 plan §Design), run by the D4
/// <c>ValidationBehavior</c> before the handler: <c>?language=</c> must be the snake_case name of a
/// known language, and the failure is reported under the wire field <c>language</c> (09 README D14).
/// </summary>
/// <remarks>
/// An absent filter is legal — all languages are listed. An <em>empty</em> one is not: <c>?language=</c>
/// is a value the client sent, and silently treating it as "no filter" would hide a bug in the picker
/// (09.4.2 plan §Failure handling).
/// </remarks>
public sealed class ListVoicesValidator : AbstractValidator<ListVoicesQuery>
{
    public ListVoicesValidator()
    {
        When(
            query => query.Language is not null,
            () => RuleFor(query => query.Language)
                .Must(value => CatalogWireNames.TryParseLanguage(value, out _))
                .WithMessage("language_unknown"));
    }
}
