using NightTales.Application.Abstractions.Persistence;

namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// The per-message send lock of the e-mail pipeline (09.2.6-T3, 09 §5.3): the job takes it before it reads
/// the row's status and releases it after the result — <c>sent</c> or <c>failed</c> — is committed, so two
/// jobs for the same <c>emailMessageId</c> can never both reach SMTP.
/// </summary>
/// <remarks>
/// <para>
/// The idempotency guard alone is not enough. Hangfire locks a job by its own id, so a redelivered outbox
/// event that enqueues <c>SendEmailJob</c> a second time produces two jobs with two ids: both can read a
/// not-yet-<c>sent</c> row and both can send. The lock is keyed by the message id, which is what makes the
/// second attempt wait for the first one's result (09.2.6-T3 failure table: "two Workers take the same
/// job").
/// </para>
/// <para>
/// It is non-blocking on purpose: a job that cannot take the lock returns and leaves the message to the
/// worker that holds it, instead of waiting on a mail server.
/// </para>
/// </remarks>
public interface IEmailSendLock
{
    /// <summary>
    /// Tries to take the send lock of one message on the job's own database connection, without waiting.
    /// </summary>
    /// <param name="database">The job's write model; its connection carries the lock and must stay open until the handle is disposed.</param>
    /// <param name="emailMessageId">The <c>email_messages</c> row being sent; the lock key.</param>
    /// <param name="cancellationToken">Cancels taking the lock.</param>
    /// <returns>
    /// A handle that releases the lock when disposed, or <see langword="null"/> when another worker already
    /// holds it (that worker owns the send).
    /// </returns>
    Task<IAsyncDisposable?> TryAcquireAsync(
        IAppDbContext database,
        Guid emailMessageId,
        CancellationToken cancellationToken);
}
