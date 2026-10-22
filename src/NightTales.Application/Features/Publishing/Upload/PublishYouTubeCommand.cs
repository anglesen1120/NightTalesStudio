using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Publishing.Upload;

/// <summary>
/// Uploads one consented publication to YouTube (06.2.3-T2 plan §4 UC1): the job's only argument is the row's id, and
/// the handler answers what the run did.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not an <c>IBaseCommand</c>.</b> The D4 pipeline's <c>UnitOfWorkBehavior</c> would open one
/// transaction around the whole handler, and this work spends minutes inside Google calls and the object stream: the
/// handler therefore owns its own three short transactions (the reserve, the session-URI write and the terminal state)
/// and the behavior must never wrap them (06 README D9, 06.2.3 plan §3.2).
/// </para>
/// <para>
/// <b>The id is the whole message.</b> The tenant comes from the scope the job pinned
/// (<c>ISystemContext.For</c>), so a command can never name another owner's row and the wire/queue payload carries no
/// title, no channel identity and no token (06 README D17, D12).
/// </para>
/// </remarks>
/// <param name="PublicationId">The <c>publications</c> row to upload.</param>
public sealed record PublishYouTubeCommand(Guid PublicationId) : IRequest<ErrorOr<UploadOutcome>>;

/// <summary>What one run of the upload did (06.2.3-T2 plan §Design "Job and command").</summary>
public enum UploadOutcome
{
    /// <summary>The video was uploaded and YouTube is transcoding it: the row is <c>processing</c>.</summary>
    Processing,

    /// <summary>The row was pushed back to <c>scheduled</c>: the quota day is spent, or an earlier attempt's retry is not due yet.</summary>
    Rescheduled,

    /// <summary>The row is held under an open platform incident and re-checks it at its deferred instant.</summary>
    HeldByIncident,

    /// <summary>The channel connection can no longer be used and the row is <c>failed</c> for good.</summary>
    ReauthRequired,

    /// <summary>The row is <c>failed</c> (a refusal no retry can fix, or the attempts were spent).</summary>
    Failed,

    /// <summary>YouTube already held the video: nothing was uploaded and the row was adopted.</summary>
    Reconciled,
}
