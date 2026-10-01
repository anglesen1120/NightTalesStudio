using ErrorOr;
using Mediator;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Series.Commands.UpdateSeries;

/// <summary>
/// <c>PATCH /api/v1/series/{id}</c> (09.4.1 plan §4 U2): the id from the route, the <c>If-Match</c>
/// header the <c>RequireIfMatch()</c> filter guaranteed, and the JSON-merge patch.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StoryPresetSpecified"/> exists because <see cref="SeriesPatch.StoryPreset"/> cannot tell
/// "the client omitted the member" from "the client sent <c>null</c>": plan §5 gives the patch merge
/// semantics (absent keeps the value) and gives <c>null</c> the meaning "clear it". Only the wire model
/// knows which one arrived, so the endpoint passes the answer here and the handler substitutes the
/// current value for an omitted member — it has loaded the series anyway.
/// </para>
/// <para>
/// The response is <see cref="Success"/> and not a DTO: the new ETag comes from the post-commit read
/// the endpoint performs (09 README D15).
/// </para>
/// </remarks>
/// <param name="Id">The series to patch; another user's id answers 404, like a missing one.</param>
/// <param name="IfMatch">The <c>If-Match</c> header; compared against the row's current ETag.</param>
/// <param name="Patch">The members the client sent.</param>
/// <param name="StoryPresetSpecified">Whether the body carried <c>storyPreset</c> at all.</param>
public sealed record UpdateSeriesCommand(
    Guid Id,
    string IfMatch,
    SeriesPatch Patch,
    bool StoryPresetSpecified) : ICommand<ErrorOr<Success>>;
