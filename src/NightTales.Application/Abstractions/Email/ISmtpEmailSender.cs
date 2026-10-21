using ErrorOr;
using MimeKit;

namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// The one place a message actually leaves the process (09 README BD9, W2-O4): the SMTP adapter the
/// Worker's <c>SendEmailJob</c> calls, and the seam every test replaces — <c>FakeSmtpEmailSender</c> in
/// <c>NightTales.TestKit</c> counts calls and captures the rendered message, so no test needs a mail
/// server (09 README D13).
/// </summary>
/// <remarks>
/// <para>
/// It takes the composed <see cref="MimeMessage"/>, not a template: rendering, addressing and the
/// deterministic <c>Message-Id</c> are the job's decisions, so the transport stays one connection, one
/// <c>SendAsync</c> and one disconnection.
/// </para>
/// <para>
/// A failure is reported twice on purpose: an SMTP-level refusal that can be described as a code comes
/// back as an <see cref="ErrorOr{T}"/> result, while a transport exception propagates so the job can
/// record it and rethrow it for Hangfire's retry policy (09 README BD9: five retries). A permanent
/// refusal (a 5xx answer) is <c>email_send_rejected</c> and a transient one (a 4xx answer) is
/// <c>email_send_failed</c>; the job fails the row without a retry for the first (09.2.6-T3 L176).
/// </para>
/// </remarks>
public interface ISmtpEmailSender
{
    /// <summary>Sends one composed message.</summary>
    /// <param name="message">The message to send; its <c>Message-Id</c> is the job's deterministic id.</param>
    /// <param name="cancellationToken">Cancels the connection, the send and the disconnection.</param>
    /// <returns>The provider message id of the accepted message, or the failure code.</returns>
    Task<ErrorOr<string>> SendAsync(MimeMessage message, CancellationToken cancellationToken);
}
