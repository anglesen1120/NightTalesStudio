namespace NightTales.Domain.Publishing;

/// <summary>
/// The D12 queue time of a publication — the instant its upload is due — as one pure function so no caller
/// ever spells the formula twice (<c>06-youtube-posting/README.md</c> D12, YR3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Who calls it.</b> Nobody in the consent command: the Api never enqueues (YR3), so the handler of
/// <c>ConsentPublicationCommand</c> only leaves the row <c>scheduled</c> and lets the outbox interceptor raise
/// <c>publication_updated.v1</c>. The caller is 06.2.3-T2's <c>PublicationScheduledForUploadHandler</c>, which
/// re-reads the row on that event and schedules <c>PublishYouTubeJob</c> at
/// <c>NextAttemptAt ?? PublicationQueueTime.For(PublishAt, PublishLeadMinutes, now)</c>. Declaring the formula here
/// (rather than in the Worker subscriber) is what keeps one owner for it, which is what YR3 asks for.
/// </para>
/// <para>
/// <b>Why the clamp.</b> D12's formula is <c>publishAt − PublishLeadMinutes</c>, but a publication whose
/// <c>publishAt</c> is already inside the lead window — created before the window shrank, or re-read after a
/// delay — must upload <em>now</em>, never in the past: a job scheduled for an instant that has already passed is
/// either rejected by the scheduler or run immediately, and only the first is a surprise. The clamp makes the
/// function total, so the caller never needs a second guard.
/// </para>
/// <para>
/// <b>What this class does not validate.</b> The range of <c>leadMinutes</c> (and of <c>publishAt</c>) is
/// <c>YouTubeOptions</c>'/D6's rule, checked at create time; a negative lead time handed in here simply answers a
/// later instant, and nothing in the Domain can decide that it is wrong.
/// </para>
/// <para>
/// <b>Placement.</b> 06.2.2-T1's Outputs list this file under <c>src/NightTales.Domain/Channels/</c>, where nothing
/// else of the publication flow lives: it sits in <c>Publishing/</c> beside the aggregate it computes the queue time
/// of, so the namespace (<see cref="NightTales.Domain.Publishing"/>) matches the folder like every other file here.
/// </para>
/// </remarks>
public static class PublicationQueueTime
{
    /// <summary>
    /// The instant the upload of one publication is due (06 README D12): <paramref name="publishAt"/> minus
    /// <paramref name="leadMinutes"/>, or <paramref name="now"/> when the owner asked for no particular time —
    /// never earlier than <paramref name="now"/>.
    /// </summary>
    /// <param name="publishAt">When the owner wants the video live, or <see langword="null"/> for "as soon as it is processed".</param>
    /// <param name="leadMinutes"><c>YouTubeOptions.PublishLeadMinutes</c>; the Domain never reads the option itself.</param>
    /// <param name="now">The caller's clock reading; the function never reads the clock.</param>
    /// <returns>The UTC instant the upload is due, at or after <paramref name="now"/>.</returns>
    public static DateTimeOffset For(DateTimeOffset? publishAt, int leadMinutes, DateTimeOffset now)
    {
        if (publishAt is null)
        {
            return now;
        }

        var due = publishAt.Value.AddMinutes(-leadMinutes);

        return due > now ? due : now;
    }
}
