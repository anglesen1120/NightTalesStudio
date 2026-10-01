using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Events;

namespace NightTales.Domain.Series;

/// <summary>
/// A series: one channel's consistent style (00 overview §6 <c>series</c>). The shell — the columns of
/// plan §3 — is 09.2.2-T1's; the behaviour (<see cref="Create"/>, <see cref="Update"/> and
/// <see cref="Archive"/>) is this task's.
/// </summary>
/// <remarks>
/// The rules the aggregate owns are the two <c>ErrorOr</c> outcomes a <c>201</c>/<c>200</c> request can
/// still hit: an archived series is never updated (409 <c>series_archived</c>) and a topic change is
/// refused while autopilot is enabled (409 <c>series_topic_locked</c>, 09 README D16/00 §6). Everything
/// the wire can check first — the 1–120 title, the voice language, the style-topic policy — is the
/// Application handlers' and their validator's (09.4.1 plan §4 U1/U2).
/// </remarks>
public sealed class Series : AggregateRoot, IUserOwned, IAuditable
{
    /// <summary>
    /// The catalogued code of "the series is archived" (09 README D17, 409). The catalogue constant is
    /// <c>NightTales.Application.Common.Errors.ErrorCodes.SeriesArchived</c>, which the Domain may not
    /// reference; the unit test <c>Series_Codes_MatchTheErrorCatalogue</c> keeps the two equal.
    /// </summary>
    public const string ArchivedCode = "series_archived";

    /// <summary>
    /// The catalogued code of "the topic cannot change while autopilot is enabled" (09 README D17,
    /// 409; the Domain half of <c>ErrorCodes.SeriesTopicLocked</c>).
    /// </summary>
    public const string TopicLockedCode = "series_topic_locked";

    private Series()
    {
    }

    public Guid UserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public Topic Topic { get; private set; }

    public Language Language { get; private set; }

    public Aspect Aspect { get; private set; }

    /// <summary>FK <c>voices</c> (the 04 catalog).</summary>
    public string VoiceId { get; private set; } = string.Empty;

    /// <summary>FK <c>style_templates</c>.</summary>
    public Guid StyleTemplateId { get; private set; }

    public string? StoryPreset { get; private set; }

    /// <summary>Placeholder FK to <c>channel_connections</c>, whose constraint 06 adds.</summary>
    public Guid? DefaultChannelId { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool AutopilotEnabled { get; private set; }

    /// <summary>The <c>autopilot_settings</c> JSON document (02.8.3-T1 owns its shape).</summary>
    public string? AutopilotSettings { get; private set; }

    public Guid? CurrentAutopilotAuthorizationId { get; private set; }

    public DateTimeOffset? AutopilotPausedAt { get; private set; }

    public string? AutopilotPauseReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a series for <paramref name="userId"/> and raises <see cref="SeriesCreated"/>
    /// (09 README D16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The voice, the style template and their cross-checks (language match, <c>StyleTopicPolicy</c>)
    /// are the caller's, because they read other rows: <c>CreateSeriesHandler</c> refuses them with the
    /// documented 400s before this factory runs (09.4.1 plan §4 U1, 03 CR-2).
    /// </para>
    /// <para>
    /// <c>Language</c> is written once, here: it cannot change afterwards (<see cref="SeriesPatch"/>
    /// carries no language, [DECISION] 09.4.1 plan §3).
    /// </para>
    /// </remarks>
    /// <param name="userId">The owner; the tenant filter of 09 README D10 compares it on every read.</param>
    /// <param name="draft">The validated request values.</param>
    /// <param name="now">The command's clock reading; the domain never reads the clock itself.</param>
    /// <returns>The new aggregate, tracked by the caller's context.</returns>
    public static Series Create(Guid userId, SeriesDraft draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var series = new Series
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Title = draft.Title.Trim(),
            Topic = draft.Topic,
            Language = draft.Language,
            Aspect = draft.Aspect,
            VoiceId = draft.VoiceId,
            StyleTemplateId = draft.StyleTemplateId,
            StoryPreset = draft.StoryPreset,
            CreatedAt = now,
            UpdatedAt = now,
        };

        series.Raise(new SeriesCreated(series.Id, userId, now));

        return series;
    }

    /// <summary>
    /// Applies the present fields of <paramref name="patch"/> (absent means unchanged, and a
    /// <see langword="null"/> <c>StoryPreset</c> clears the column).
    /// </summary>
    /// <remarks>
    /// The two outcomes this method can answer, in this order: an archived series is never updated
    /// (409 <see cref="ArchivedCode"/>), and a topic change is refused while autopilot is enabled
    /// (409 <see cref="TopicLockedCode"/>). The Application handler re-checks the voice and the style
    /// template after this call only for the fields the patch carries (09.4.1 plan §4 U2).
    /// </remarks>
    /// <param name="patch">The values the client sent; every member may be absent.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 the request must be answered with.</returns>
    public ErrorOr<Success> Update(SeriesPatch patch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (ArchivedAt is not null)
        {
            return Error.Conflict(ArchivedCode, "An archived series cannot be changed.");
        }

        if (patch.Topic is { } topic && topic != Topic && AutopilotEnabled)
        {
            return Error.Conflict(
                TopicLockedCode,
                "The topic cannot change while autopilot is enabled; turn autopilot off first.");
        }

        if (patch.Title is { } title)
        {
            Title = title.Trim();
        }

        if (patch.Topic is { } newTopic)
        {
            Topic = newTopic;
        }

        if (patch.Aspect is { } aspect)
        {
            Aspect = aspect;
        }

        if (patch.VoiceId is { } voiceId)
        {
            VoiceId = voiceId;
        }

        if (patch.StyleTemplateId is { } styleTemplateId)
        {
            StyleTemplateId = styleTemplateId;
        }

        // [DECISION] A null story preset clears the column; there is no "absent" for it on the wire
        // (09.4.1 plan §5: `null` for `storyPreset` clears it).
        StoryPreset = patch.StoryPreset;

        UpdatedAt = now;

        return Result.Success;
    }

    /// <summary>
    /// Archives the series: <c>archived_at</c> is set (00 overview §6), it disappears from
    /// <c>GET /series</c>, and its episodes stay readable.
    /// </summary>
    /// <remarks>
    /// Idempotent: a second call answers <c>Success</c> without raising a second
    /// <see cref="SeriesArchived"/>, exactly like the <c>DELETE</c> the client can repeat.
    /// </remarks>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>Always <c>Success</c>: there is no state an archive can be refused from.</returns>
    public ErrorOr<Success> Archive(DateTimeOffset now)
    {
        if (ArchivedAt is not null)
        {
            return Result.Success;
        }

        ArchivedAt = now;
        UpdatedAt = now;

        Raise(new SeriesArchived(Id, UserId, now));

        return Result.Success;
    }
}

/// <summary>
/// The creation values of <see cref="Series.Create"/> (09.4.1 plan §3). Language is part of the draft
/// and not of <see cref="SeriesPatch"/>: it cannot change after creation ([DECISION] plan §3).
/// </summary>
/// <param name="Title">The series title, 1–120 characters after trimming (00 §6, 09 README D16).</param>
/// <param name="Topic">The narrative topic; a true-crime series needs a true-crime style template.</param>
/// <param name="Language">The content language of the whole series; immutable after creation.</param>
/// <param name="Aspect">The video aspect every episode inherits.</param>
/// <param name="VoiceId">The 04 catalog voice; its language must equal <paramref name="Language"/>.</param>
/// <param name="StyleTemplateId">The 03 catalog style template, checked against the topic.</param>
/// <param name="StoryPreset">The 02 prompt-catalog preset key, or <see langword="null"/>.</param>
public sealed record SeriesDraft(
    string Title,
    Topic Topic,
    Language Language,
    Aspect Aspect,
    string VoiceId,
    Guid StyleTemplateId,
    string? StoryPreset);

/// <summary>
/// The JSON-merge patch of <c>PATCH /series/{id}</c> (09.4.1 plan §3): an absent member leaves the
/// column as it is, and the one member that must be clearable — <c>storyPreset</c> — treats
/// <see langword="null"/> as "clear it".
/// </summary>
/// <param name="Title">The new title, or <see langword="null"/> to keep the current one.</param>
/// <param name="Topic">The new topic, or <see langword="null"/> to keep the current one.</param>
/// <param name="Aspect">The new aspect, or <see langword="null"/> to keep the current one.</param>
/// <param name="VoiceId">The new voice, or <see langword="null"/> to keep the current one.</param>
/// <param name="StyleTemplateId">The new style template, or <see langword="null"/> to keep the current one.</param>
/// <param name="StoryPreset">The new preset key; <see langword="null"/> clears the column.</param>
public sealed record SeriesPatch(
    string? Title,
    Topic? Topic,
    Aspect? Aspect,
    string? VoiceId,
    Guid? StyleTemplateId,
    string? StoryPreset);
