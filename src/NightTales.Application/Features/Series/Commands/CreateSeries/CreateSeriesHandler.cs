using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Catalog;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

// The feature namespace `…Features.Series` shadows the aggregate's name inside this folder, so the
// entity is aliased (09.4.1-T1).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Series.Commands.CreateSeries;

/// <summary>
/// <c>POST /api/v1/series</c> (09.4.1 plan §4 U1, Design): it checks the two catalog rows the series
/// points at, builds the aggregate and lets the D4 pipeline commit it together with its
/// <c>series_created.v1</c> outbox row.
/// </summary>
/// <remarks>
/// <para>
/// The order of the checks is the order the contract documents them: the voice (exists, active, then
/// its language against the series language), the style template (exists, active, then the
/// <c>StyleTopicPolicy</c> of 03 CR-2) and the story preset (a key the 02 prompt catalog knows, which
/// is why the 400 field is <c>storyPreset</c> and not a shape error of the validator: only the
/// handler can ask a catalog).
/// </para>
/// <para>
/// The preset is refused rather than dropped: a series that stores a key the writer cannot load would
/// fail much later, in 02's generation, where the client can no longer see why.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="currentUser">The owner of the new series (09 README D10).</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="storyPresets">The 02 prompt catalog; wave 1 knows no key (09.4.1 plan §4 U1).</param>
public sealed class CreateSeriesHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IStoryPresetCatalog storyPresets) : ICommandHandler<CreateSeriesCommand, ErrorOr<SeriesCreated>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<SeriesCreated>> Handle(
        CreateSeriesCommand command,
        CancellationToken cancellationToken)
    {
        var voice = await db.Voices
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.VoiceId, cancellationToken)
            .ConfigureAwait(false);

        if (voice is null || !voice.Active)
        {
            return Errors.ValidationFailed(
                "voiceId",
                "The voice does not exist or is not active.");
        }

        if (voice.Language != command.Language)
        {
            return Error.Validation(
                ErrorCodes.VoiceLanguageMismatch,
                "The voice's language differs from the series language.");
        }

        var template = await db.StyleTemplates
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.StyleTemplateId, cancellationToken)
            .ConfigureAwait(false);

        if (template is null || !template.Active)
        {
            return Errors.ValidationFailed(
                "styleTemplateId",
                "The style template does not exist or is not active.");
        }

        if (!StyleTopicPolicy.IsAllowed(command.Topic, template.TopicHint))
        {
            // 03 CR-2: a true-crime series needs a true-crime template, and a fiction series may not
            // use one. The field is the template the client picked, not the topic.
            return Errors.ValidationFailed(
                "styleTemplateId",
                "The style template does not match the series topic.");
        }

        if (command.StoryPreset is { } preset && !storyPresets.IsKnown(preset))
        {
            return Errors.ValidationFailed(
                "storyPreset",
                "The story preset is not a known prompt-catalog preset.");
        }

        var series = SeriesAggregate.Create(
            currentUser.Id!.Value,
            new SeriesDraft(
                command.Title,
                command.Topic,
                command.Language,
                command.Aspect,
                command.VoiceId,
                command.StyleTemplateId,
                command.StoryPreset),
            clock.UtcNow);

        db.Series.Add(series);

        return new SeriesCreated(series.Id);
    }
}
