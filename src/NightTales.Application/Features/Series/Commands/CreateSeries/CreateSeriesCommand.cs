using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Series.Commands.CreateSeries;

/// <summary>
/// <c>POST /api/v1/series</c> (09.4.1 plan §4 U1): the frozen create body. The endpoint binds it
/// directly, so every member is a wire name (camelCase), and the enum members travel snake_case
/// (09 README D14).
/// </summary>
/// <remarks>
/// The response is the created id only: <c>xmin</c> — the ETag source — is assigned by PostgreSQL at
/// commit, after the handler returned, so the endpoint reads it back with the post-commit delegate of
/// <c>ToCreatedResult</c> (09 README D15, 09.4.1 plan §Design).
/// </remarks>
/// <param name="Title">1–120 characters after trimming (00 §6, 09 README D16).</param>
/// <param name="Topic">The narrative topic; <c>true_crime</c> needs a true-crime style template.</param>
/// <param name="Language">The series language; it must equal the voice's language.</param>
/// <param name="Aspect">The video aspect.</param>
/// <param name="VoiceId">A <c>voices</c> row that exists and is active.</param>
/// <param name="StyleTemplateId">A <c>style_templates</c> row that exists, is active and matches the topic.</param>
/// <param name="StoryPreset">A known 02 preset key, or <see langword="null"/>.</param>
[Audited("series.created", Category.Episodes, Visibility.User)]
public sealed record CreateSeriesCommand(
    string Title,
    Topic Topic,
    Language Language,
    Aspect Aspect,
    string VoiceId,
    Guid StyleTemplateId,
    string? StoryPreset) : ICommand<ErrorOr<SeriesCreated>>, IAuditData
{
    /// <summary>
    /// The row's projection (09 README BD15): the subject is the caller, so it stays null and the
    /// behaviour records the authenticated user. A create names no entity id — the handler assigns the
    /// UUIDv7 after this record was built — and carries no summary: the title is user text that BD16
    /// keeps out of <c>data</c>, and every other member is a column of the row the action already names.
    /// </summary>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData ToAuditData() => new(null, "series", null, AuditData.NoSummary);
}

/// <summary>
/// The created series: its id. The <c>ETag</c> is added by the endpoint after the commit (09 README
/// D15); no DTO is read here, so the response carries nothing the caller must not see twice.
/// </summary>
/// <param name="Id">The new series id, which is also the <c>Location</c> it can be read at.</param>
public sealed record SeriesCreated(Guid Id);
