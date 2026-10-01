using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Catalog;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

// The feature namespace `…Features.Series` shadows the aggregate's name inside this folder, so the
// entity is aliased (09.4.1-T1).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Series.Commands.UpdateSeries;

/// <summary>
/// <c>PATCH /api/v1/series/{id}</c> (09.4.1 plan §4 U2, Design): load through the tenant filter, compare
/// <c>If-Match</c>, re-check the catalog rows the patch touches, then let the aggregate decide.
/// </summary>
/// <remarks>
/// The order is the contract's: 404 before 412 before 400 before 409. A caller who did not read the row
/// must not learn anything about it, and a caller with a stale tag must not be told which field was
/// wrong — the row moved, so the request is answered 412 <c>series_conflict</c> without a field.
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="storyPresets">The 02 prompt catalog; wave 1 knows no key (09.4.1 plan §4 U1).</param>
public sealed class UpdateSeriesHandler(IAppDbContext db, IClock clock, IStoryPresetCatalog storyPresets)
    : ICommandHandler<UpdateSeriesCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        UpdateSeriesCommand command,
        CancellationToken cancellationToken)
    {
        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (series is null)
        {
            // D10: another user's series and a series that does not exist are the same answer.
            return Errors.NotFound();
        }

        if (!ETags.Matches(command.IfMatch, ETags.Series(series.Version)))
        {
            // 412 through the code catalogue (09 README D7/D15, F15). The route filter already answered
            // 428 for a missing header; this covers a stale one and a direct handler call.
            return Error.Failure(
                ErrorCodes.SeriesConflict,
                "The series changed since it was read; read it again and retry.");
        }

        var voiceFailure = await CheckVoiceAsync(series, command.Patch.VoiceId, cancellationToken)
            .ConfigureAwait(false);
        if (voiceFailure is { } voiceError)
        {
            return voiceError;
        }

        var templateFailure = await CheckStyleTemplateAsync(series, command.Patch.Topic, command.Patch.StyleTemplateId, cancellationToken)
            .ConfigureAwait(false);
        if (templateFailure is { } templateError)
        {
            return templateError;
        }

        if (command.Patch.StoryPreset is { } preset && !storyPresets.IsKnown(preset))
        {
            return Errors.ValidationFailed(
                "storyPreset",
                "The story preset is not a known prompt-catalog preset.");
        }

        // Plan §5: an omitted storyPreset keeps the stored one; the wire model is the only layer that
        // knows it was omitted, so the absence arrives as `false` here and the current value is used.
        var patch = command.StoryPresetSpecified
            ? command.Patch
            : command.Patch with { StoryPreset = series.StoryPreset };

        return series.Update(patch, clock.UtcNow);
    }

    /// <summary>
    /// The U1 voice rules for a patch that names another voice: it must exist, be active and speak the
    /// series language. Sending the voice the series already has is a no-op and is accepted even when
    /// that voice has since been deactivated — existing series keep it (09.4.1 plan §Failure handling).
    /// </summary>
    private async Task<Error?> CheckVoiceAsync(SeriesAggregate series, string? voiceId, CancellationToken cancellationToken)
    {
        if (voiceId is null || string.Equals(voiceId, series.VoiceId, StringComparison.Ordinal))
        {
            return null;
        }

        var voice = await db.Voices
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == voiceId, cancellationToken)
            .ConfigureAwait(false);

        if (voice is null || !voice.Active)
        {
            return Errors.ValidationFailed("voiceId", "The voice does not exist or is not active.");
        }

        return voice.Language != series.Language
            ? Error.Validation(
                ErrorCodes.VoiceLanguageMismatch,
                "The voice's language differs from the series language.")
            : null;
    }

    /// <summary>
    /// The U1 style-template rules for a patch that changes the template or the topic (09.4.1 plan
    /// §Design: "re-checks the policy when the topic or the template changes"): the effective pair —
    /// the patched values falling back to the stored ones — must satisfy
    /// <see cref="StyleTopicPolicy"/> (03 CR-2).
    /// </summary>
    private async Task<Error?> CheckStyleTemplateAsync(
        SeriesAggregate series,
        Topic? topic,
        Guid? styleTemplateId,
        CancellationToken cancellationToken)
    {
        var templateChanges = styleTemplateId is { } candidate && candidate != series.StyleTemplateId;
        var topicChanges = topic is { } newTopic && newTopic != series.Topic;

        if (!templateChanges && !topicChanges)
        {
            return null;
        }

        Topic? topicHint;
        if (templateChanges)
        {
            var template = await db.StyleTemplates
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == styleTemplateId, cancellationToken)
                .ConfigureAwait(false);

            if (template is null || !template.Active)
            {
                return Errors.ValidationFailed(
                    "styleTemplateId",
                    "The style template does not exist or is not active.");
            }

            topicHint = template.TopicHint;
        }
        else
        {
            // Only the topic moved: the stored template decides whether it may still be used with it.
            topicHint = await db.StyleTemplates
                .AsNoTracking()
                .Where(candidate => candidate.Id == series.StyleTemplateId)
                .Select(candidate => candidate.TopicHint)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return StyleTopicPolicy.IsAllowed(topic ?? series.Topic, topicHint)
            ? null
            : Errors.ValidationFailed(
                "styleTemplateId",
                "The style template does not match the series topic.");
    }
}
