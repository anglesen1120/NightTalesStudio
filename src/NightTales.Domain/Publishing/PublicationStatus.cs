namespace NightTales.Domain.Publishing;

/// <summary>
/// The life cycle of one <c>publications</c> row (06 README D6/D12): stored as its snake_case wire value
/// (<c>awaiting_consent</c>, never <c>AwaitingConsent</c>) and guarded by <c>ck_publications_status</c>, so the
/// C# member, the column and the <c>CHECK</c> cannot drift.
/// </summary>
/// <remarks>
/// <see cref="Failed"/> and <see cref="Cancelled"/> are terminal: they are the two states an episode's partial
/// unique <c>ux_publications_active_episode_platform</c> excludes, so a later create for the same
/// <c>(episode_id, platform)</c> may write a new row.
/// </remarks>
public enum PublicationStatus
{
    /// <summary>
    /// Created and waiting for a consent (06 README D7/D8): the owner's manual consent, or the end of the
    /// veto window on an autopilot publication. Nothing is queued for upload yet.
    /// </summary>
    AwaitingConsent,

    /// <summary>
    /// Consented, so the upload may be queued at <c>PublicationQueueTime.For(publishAt, PublishLeadMinutes, now)</c>
    /// (06 README D12). The row keeps <c>next_attempt_at</c> for the retry schedule.
    /// </summary>
    Scheduled,

    /// <summary>
    /// The resumable upload runs (06 README D9). <c>Publication.StartUpload</c> is idempotent here — a lost
    /// response retries into the probe/resume step (06 YR18).
    /// </summary>
    Uploading,

    /// <summary>YouTube accepted the upload and processes it; <c>PollYouTubeProcessingJob</c> watches it (06 README D12).</summary>
    Processing,

    /// <summary>The video is live on the channel (<c>external_post_id</c>/<c>external_url</c>/<c>published_at</c> are set).</summary>
    Published,

    /// <summary>
    /// The publication gave up (06 README D12). <c>error_code</c> names the reason, and the row left the partial
    /// unique, so the episode may be published again.
    /// </summary>
    Failed,

    /// <summary>Cancelled before the upload started; the row left the partial unique, so the episode may be published again.</summary>
    Cancelled,
}
