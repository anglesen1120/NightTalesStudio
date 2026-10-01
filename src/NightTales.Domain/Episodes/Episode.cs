using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes.Events;

namespace NightTales.Domain.Episodes;

/// <summary>
/// An episode: the central aggregate (00 overview §6 <c>episodes</c>). 09.2.2-T1 owns the shell — the
/// columns of plan §3; this file carries the behaviour of 09.5.1-T1 (<see cref="Create"/>,
/// <see cref="Cancel"/> and the D16 events). The partial files of plan 02 (<c>Episode.Story.cs</c>,
/// <c>Episode.FactSheet.cs</c>, …) extend the same class.
/// </summary>
public sealed partial class Episode : AggregateRoot, IUserOwned, IAuditable
{
    /// <summary>
    /// The catalogued code of "the transition is not allowed in this state" (409, 02 D8 / 09 README
    /// D16/D17). The catalogue constant is
    /// <c>NightTales.Application.Common.Errors.ErrorCodes.EpisodeInvalidState</c>, which the Domain may
    /// not reference; the unit test <c>Episode_InvalidStateCode_MatchesTheErrorCatalogue</c> keeps the
    /// two equal.
    /// </summary>
    public const string InvalidStateCode = "episode_invalid_state";

    private Episode()
    {
    }

    public Guid UserId { get; private set; }

    public Guid SeriesId { get; private set; }

    public string? Title { get; private set; }

    public string Brief { get; private set; } = string.Empty;

    public LengthTier LengthTier { get; private set; }

    public EpisodeStatus Status { get; private set; }

    public Guid? CurrentStoryVersionId { get; private set; }

    public Guid? ApprovedStoryVersionId { get; private set; }

    public Guid? FinalVideoAssetId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates an episode of <paramref name="seriesId"/> for <paramref name="userId"/> in
    /// <see cref="EpisodeStatus.Draft"/> and raises <see cref="EpisodeCreated"/> (09 README D16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both ids come from the <em>loaded series</em>, never from the request body (09.5.1 plan §4 UC1,
    /// §8): the Application handler reads the series through the D10 tenant filter and passes its
    /// <c>UserId</c>/<c>Id</c> here, so a client can neither create an episode for another tenant nor
    /// point one at a series id it did not name in the route.
    /// </para>
    /// <para>
    /// The caller owns the trimming: <c>CreateEpisodeValidator</c> bounds the <em>trimmed</em> brief and
    /// title and <c>CreateEpisodeHandler</c> trims before calling this factory, so what is stored is what
    /// was validated.
    /// </para>
    /// </remarks>
    /// <param name="userId">The owner; the tenant filter of 09 README D10 compares it on every read.</param>
    /// <param name="seriesId">The series the episode belongs to.</param>
    /// <param name="title">The optional title, already trimmed and empty-normalized to <see langword="null"/>.</param>
    /// <param name="brief">The brief, already trimmed, 1–1,000 characters.</param>
    /// <param name="tier">The story length tier.</param>
    /// <param name="now">The command's clock reading; the domain never reads the clock itself.</param>
    /// <returns>The new aggregate, tracked by the caller's context.</returns>
    public static Episode Create(
        Guid userId,
        Guid seriesId,
        string? title,
        string brief,
        LengthTier tier,
        DateTimeOffset now)
    {
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            SeriesId = seriesId,
            Title = title,
            Brief = brief,
            LengthTier = tier,
            Status = EpisodeStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };

        episode.Raise(new EpisodeCreated(episode.Id, seriesId, userId, now));

        return episode;
    }

    /// <summary>
    /// Cancels the episode (00 §7 "cancelled (user deletes)", 09 README D16): the row is kept, its reads
    /// answer 404 (<c>DELETE /episodes/{id}</c> is a soft cancel, F14), and
    /// <see cref="EpisodeCancelled"/> tells the Worker to schedule the episode's assets for retention.
    /// </summary>
    /// <remarks>
    /// The four states that own in-flight work refuse the transition with 409
    /// <see cref="InvalidStateCode"/>: cancelling a <c>story_generating</c> or <c>rendering</c> episode
    /// would race the generator/renderer (02 owns <c>LockEpisodeForUpdateAsync</c>), a
    /// <c>published</c> episode is already public, and a <c>cancelled</c> one is not found — cancel is
    /// not idempotent, exactly as D16 fixes it. Every other state cancels and reports its previous
    /// status so the handler can act on where the episode was.
    /// </remarks>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 the request must be answered with.</returns>
    public ErrorOr<Success> Cancel(DateTimeOffset now)
    {
        if (Status is EpisodeStatus.StoryGenerating
            or EpisodeStatus.Rendering
            or EpisodeStatus.Published
            or EpisodeStatus.Cancelled)
        {
            return Error.Conflict(
                InvalidStateCode,
                "The episode cannot be cancelled in its current state.");
        }

        var previousStatus = Status;
        Status = EpisodeStatus.Cancelled;
        UpdatedAt = now;

        Raise(new EpisodeCancelled(Id, SeriesId, UserId, previousStatus, now));

        return Result.Success;
    }
}
