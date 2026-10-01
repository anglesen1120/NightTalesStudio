using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Features.Email.Jobs;
using NightTales.Domain.Communication;

namespace NightTales.Application.Features.Email.Outbox;

/// <summary>
/// Delivers <c>email_requested.v1</c> by enqueueing <c>SendEmailJob</c> (09 README D11/D12, 09.2.6-T3).
/// </summary>
/// <remarks>
/// <para>
/// It only enqueues: the message is sent in the Worker's job on the <c>email</c> queue, and the outbox row
/// is marked processed as soon as the job is queued, so a slow SMTP server never holds the outbox row's
/// lease. The argument is the <c>email_messages</c> id alone (09 README D12: job arguments are stored in
/// plain text in the <c>hangfire</c> schema), and the row — not this payload — owns the render values,
/// which is what makes a retry days later still correct.
/// </para>
/// <para>
/// Delivery is at least once (09 README D11): a redelivered event enqueues a second job for the same row,
/// and the row's <c>sent</c> guard is what keeps that from sending twice.
/// </para>
/// </remarks>
/// <param name="scheduler">The Worker's job port; the Api never resolves this handler.</param>
public sealed class EmailRequestedHandler(IJobScheduler scheduler) : IOutboxHandler<EmailRequested>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(
        EmailRequested domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        scheduler.Enqueue<SendEmailJob>((job, ct) => job.RunAsync(domainEvent.EmailMessageId, ct));

        return ValueTask.CompletedTask;
    }
}
