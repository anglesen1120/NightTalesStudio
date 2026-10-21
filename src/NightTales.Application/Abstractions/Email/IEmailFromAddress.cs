using MimeKit;

namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// The sender identity every message is composed with (09 README BD9): <c>Email:From</c>, the mailbox the
/// platform sends from.
/// </summary>
/// <remarks>
/// <para>
/// It is a port for the same reason <see cref="IEmailLinkOrigin"/> is: the composed message is the job's
/// work, but the identity it is sent as is deployment configuration, and the Application layer reads no
/// configuration. The mail server rejects a message without a <c>From</c>, so the host validates the value
/// at start-up (<c>Email:From:Address</c> is required) rather than failing inside a job.
/// </para>
/// </remarks>
public interface IEmailFromAddress
{
    /// <summary>The <c>From</c> mailbox: a required address and the display name the platform uses.</summary>
    MailboxAddress From { get; }
}
