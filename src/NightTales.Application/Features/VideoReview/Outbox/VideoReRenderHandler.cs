using Mediator;
using NightTales.Application.Abstractions.Etags;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Rendering.Commands.RenderEpisode;
using NightTales.Domain.Episodes;
using NightTales.Domain.Episodes.Events;

namespace NightTales.Application.Features.VideoReview.Outbox;

/// <summary>
/// Turns a <c>video_rejected.v1</c> whose target is <see cref="RejectTarget.ReRender"/> into the new render of
/// the already-approved story (09 README §B10 WT3), in the delivery's own scope and through the D4 pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The rejection committed before this runs, so the render's spend is never inside the rejecting transaction: a
/// render that fails (a flagged keyword, the spend cap, no dispatchable node) leaves the episode in
/// <c>story_approved</c>, which is the state the UI already offers "render again" from.
/// </para>
/// <para>
/// The render's precondition is the episode's ETag read at send time (<see cref="IEpisodeEtagReader"/>), not a
/// value carried in the event: the rejection bumped <c>xmin</c>, so any tag captured earlier would be stale and
/// fail the render's precondition. The read and the send are milliseconds apart in this handler, so the
/// send-time read is the current tag; only a write landing in that window makes the precondition stale and the
/// render answer the retryable 412.
/// </para>
/// <para>
/// The idempotency key is <c>video-reject-rerender:{messageId}</c> and the row id is stable across the
/// dispatcher's retries (D11), so one rejection reserves the credits of one re-render however often the send is
/// retried. A reused-key refusal is <em>not</em> an error: the key is claimed under the hash of the request it
/// sent, the tag is read fresh on every delivery and the render itself moves <c>xmin</c>, so a redelivery after
/// the first send committed can only ever be refused that way — and it means the work is already done. Every
/// other error result is surfaced: throwing leaves the outbox row due so the dispatcher retries with backoff
/// (there is no retry loop here), and a permanent refusal (a stale tag, a state that moved, the spend cap) ends
/// in a dead letter with the error codes only — never content (D17).
/// </para>
/// </remarks>
/// <param name="sender">The in-process mediator of the delivery's scope.</param>
/// <param name="episodeEtag">The episode ETag read the render command takes as its <c>If-Match</c>.</param>
public sealed class VideoReRenderHandler(ISender sender, IEpisodeEtagReader episodeEtag) : IOutboxHandler<VideoRejected>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(
        VideoRejected domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Target is not RejectTarget.ReRender)
        {
            // The `story` target sends the owner back to the editor: there is nothing to render.
            return;
        }

        var etag = await episodeEtag
            .ForEpisodeAsync(domainEvent.EpisodeId, cancellationToken)
            .ConfigureAwait(false);

        var result = await sender
            .Send(
                new RenderEpisodeCommand(
                    domainEvent.EpisodeId,
                    IfMatch: etag,
                    IdempotencyKey: $"video-reject-rerender:{context.MessageId:N}"),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsError && result.Errors.All(error => error.Code == ErrorCodes.IdempotencyKeyReused))
        {
            // A redelivery after the first send committed (the ack was lost): the stored claim of this
            // message's key was made with the ETag read then, and the render itself moved xmin, so the
            // canonical request hash no longer matches. The key is derived from the message id alone, so a
            // reused-key refusal here means this rejection's render already ran: done, not a failure.
            return;
        }

        if (result.IsError)
        {
            // The codes only, never content: the dispatcher stores this text in the outbox row's `error`
            // column (09 README D17).
            throw new InvalidOperationException(
                "RenderEpisodeCommand failed: "
                + string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }
}
