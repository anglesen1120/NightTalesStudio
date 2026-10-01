using ErrorOr;
using Mediator;
using NightTales.Application.Features.Catalog.Dtos;

namespace NightTales.Application.Features.Catalog.Queries.ListVoices;

/// <summary>
/// <c>GET /api/v1/voices?language=</c> (09.4.2 plan §4 UC1, §5): the selectable voices of the 04
/// catalog, newest seed first — the whole closed set, so there is no cursor and no limit
/// ([DECISION] plan §5).
/// </summary>
/// <remarks>
/// The filter is the raw wire string and not a <see cref="NightTales.Domain.Common.Language"/>: an
/// unknown value has to answer 400 <c>validation_failed</c> under field <c>language</c> instead of
/// being silently dropped or refused by the model binder (09 README D14). The validator before the
/// handler turns it into the enum.
/// </remarks>
/// <param name="Language">The <c>vi</c>/<c>en</c> filter, or <see langword="null"/> for every language.</param>
public sealed record ListVoicesQuery(string? Language)
    : IQuery<ErrorOr<IReadOnlyList<VoiceListItemDto>>>;
