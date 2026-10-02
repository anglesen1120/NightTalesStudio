namespace NightTales.Domain.Communication;

/// <summary>
/// Delivery state of one queued e-mail (09 README BD9, 09.2.6-T3): stored as snake_case text with a
/// <c>CHECK</c> on the value set (09 README D8), so the column can never hold a fourth state.
/// </summary>
/// <remarks>
/// <see cref="Queued"/> is the state the producer's transaction commits and the state a retry sits in
/// between two attempts; <see cref="Sent"/> is terminal and is the idempotency guard of
/// <c>SendEmailJob</c> (09 §5.3); <see cref="Failed"/> is terminal for the job run — Hangfire owns the
/// retries (5, <see cref="NightTales.Application.Abstractions.Jobs.JobQueue.Email"/>) and an operator can
/// re-run a failed row from the dashboard (09.2.6-T2).
/// </remarks>
public enum EmailStatus
{
    /// <summary>Recorded, waiting for <c>SendEmailJob</c> — also the state of an in-flight attempt.</summary>
    Queued,

    /// <summary>Handed to the SMTP server; the job never sends this row again.</summary>
    Sent,

    /// <summary>The attempt failed; <c>last_error_code</c> says why.</summary>
    Failed,
}
