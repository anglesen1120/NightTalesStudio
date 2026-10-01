using FluentValidation;
using NightTales.Application.Common.Paging;

namespace NightTales.Application.Features.Episodes.Queries.ListEpisodes;

/// <summary>
/// Shape validation of <c>GET /api/v1/episodes</c> (09.5.1 plan §Design), run by the D4
/// <c>ValidationBehavior</c> before the handler: the page size's 1–100 range and the cursor's encoding,
/// each reported under the wire field the client must fix (<c>limit</c> / <c>cursor</c>).
/// </summary>
/// <remarks>
/// <para>
/// The <c>seriesId</c> and <c>status</c> filters are not validated here: an unknown id is simply a page
/// with no rows (the tenant filter already hides other users' series, so 404 would leak their existence),
/// and an unknown status name never reaches this validator — the endpoint parses it off the query string
/// and answers before the message is sent.
/// </para>
/// <para>
/// An empty <c>?cursor=</c> is "no cursor", not a failure: that is <see cref="Cursor.TryDecode"/>'s own
/// contract for the shared D14 type. The handler agrees, so the route never 400s on an empty value.
/// </para>
/// </remarks>
public sealed class ListEpisodesValidator : AbstractValidator<ListEpisodesQuery>
{
    public ListEpisodesValidator()
    {
        RuleFor(query => query.Limit)
            .InclusiveBetween(1, ListEpisodesQuery.MaxLimit)
            .WithMessage("limit_out_of_range");

        RuleFor(query => query.Cursor)
            .Must(BeAbsentOrDecodable)
            .WithMessage("cursor_invalid");
    }

    private static bool BeAbsentOrDecodable(string? cursor) =>
        string.IsNullOrEmpty(cursor) || Cursor.TryDecode(cursor, out _);
}
