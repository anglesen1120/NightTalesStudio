using NightTales.Domain.Communication;

namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// The delivery state of one queued e-mail (09 README BD9), read by callers that must know before they
/// act — 06.2.2-T3's veto notice, 02.8.2's and 02.8.3's owner alerts.
/// </summary>
/// <param name="Id">The <c>email_messages.id</c> the queue call returned.</param>
/// <param name="Status">The state after the last attempt that committed.</param>
/// <param name="Attempts">How many attempts started; a message that was never attempted is <c>0</c>.</param>
/// <param name="LastErrorCode">Why the last attempt failed, or <see langword="null"/>.</param>
/// <param name="SentAt">When the SMTP server accepted it, or <see langword="null"/>.</param>
/// <param name="CreatedAt">When it was queued.</param>
public sealed record EmailDelivery(
    Guid Id,
    EmailStatus Status,
    int Attempts,
    string? LastErrorCode,
    DateTimeOffset? SentAt,
    DateTimeOffset CreatedAt);
