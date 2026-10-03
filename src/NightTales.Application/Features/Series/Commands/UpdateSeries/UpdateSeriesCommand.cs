using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;
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
[Audited("series.updated", Category.Episodes, Visibility.User)]
public sealed record UpdateSeriesCommand(
    Guid Id,
    string IfMatch,
    SeriesPatch Patch,
    bool StoryPresetSpecified) : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <summary>
    /// The row's projection (09 README BD15): the patched series and the wire names of the members the
    /// client sent, comma-joined. The values are deliberately absent — a title is user text and BD16 keeps
    /// content out of <c>data</c>, exactly as 09.3.4 plan §5 rules for the same summary shape.
    /// </summary>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData ToAuditData() => new(
        SubjectUserId: null,
        EntityType: "series",
        EntityId: Id,
        Summary: new Dictionary<string, object>(StringComparer.Ordinal) { ["fields"] = ChangedFields() });

    /// <summary>The wire names of the members the patch carries, in the order the patch declares them.</summary>
    private string ChangedFields()
    {
        var fields = new List<string>(6);

        if (Patch.Title is not null)
        {
            fields.Add("title");
        }

        if (Patch.Topic is not null)
        {
            fields.Add("topic");
        }

        if (Patch.Aspect is not null)
        {
            fields.Add("aspect");
        }

        if (Patch.VoiceId is not null)
        {
            fields.Add("voiceId");
        }

        if (Patch.StyleTemplateId is not null)
        {
            fields.Add("styleTemplateId");
        }

        if (StoryPresetSpecified)
        {
            fields.Add("storyPreset");
        }

        return string.Join(',', fields);
    }
}
