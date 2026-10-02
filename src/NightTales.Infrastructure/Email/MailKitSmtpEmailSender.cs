using ErrorOr;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Common.Errors;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// Sends one composed message over SMTP with MailKit (09 README BD9, W2-O4): connect, authenticate when
/// credentials are configured, send, disconnect.
/// </summary>
/// <remarks>
/// <para>
/// One client per call, with the configured timeout, so a hung mail server can delay exactly one job
/// attempt and never holds a pooled connection of an idle Worker. The transport security comes from
/// <c>Email:Smtp:Security</c>, which the start-up validation refuses to leave at <c>None</c> outside
/// Development.
/// </para>
/// <para>
/// A refusal the server can describe (an SMTP status code) comes back as a failure result whose description
/// carries the status code only — never the server's text, which echoes the recipient and must not reach a
/// log (09 README BD16). A 5xx answers <c>email_send_rejected</c> (permanent: the job fails the row without
/// a retry) and a 4xx <c>email_send_failed</c> (transient: the row stays retryable). Everything else (an
/// unreachable host, a TLS failure, a timeout) propagates as the exception MailKit raised, so the job
/// records the attempt and rethrows it for the five-attempt policy of the <c>email</c> queue.
/// </para>
/// <para>
/// 09.10.2-T3 (09 W3 CR-29) inserts its connect-time egress guard into this dial path: the resolved
/// endpoint is checked before the connection is opened. That guard is a wave-3 type and does not exist in
/// this build, so this adapter dials the configured host directly.
/// </para>
/// </remarks>
/// <param name="options">The <c>Email</c> section; its <c>Smtp</c> block is already validated for this environment.</param>
internal sealed class MailKitSmtpEmailSender(IOptions<EmailOptions> options) : ISmtpEmailSender
{
    /// <inheritdoc />
    public async Task<ErrorOr<string>> SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var smtp = options.Value.Smtp;

        // The raw header, not `MimeMessage.MessageId`: MailKit normalises the latter to the bare
        // `local@domain`, while the deterministic id the job composed — and the one the wire carries —
        // is the bracketed form of BD9.
        var providerMessageId = message.Headers[HeaderId.MessageId];

        using var client = new SmtpClient
        {
            Timeout = (int)TimeSpan.FromSeconds(smtp.TimeoutSeconds).TotalMilliseconds,
        };

        await client
            .ConnectAsync(smtp.Host, smtp.Port, SecurityOf(smtp.Security), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (!string.IsNullOrWhiteSpace(smtp.Username))
            {
                // The start-up validation makes the pair all-or-nothing, so a username implies a password.
                await client
                    .AuthenticateAsync(smtp.Username, smtp.Password!, cancellationToken)
                    .ConfigureAwait(false);
            }

            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (SmtpCommandException exception)
        {
            // A 5xx refusal is permanent — a second attempt cannot change the answer — while a 4xx (a
            // greylist, a mailbox that is momentarily full) stays retryable (09.2.6-T3 L176).
            return Error.Failure(
                code: IsPermanent(exception.StatusCode) ? ErrorCodes.EmailSendRejected : ErrorCodes.EmailSendFailed,
                description: $"The SMTP server refused the message (status code {exception.StatusCode}).");
        }
        finally
        {
            if (client.IsConnected)
            {
                // The QUIT runs on an uncancellable token: if the job's token fires between a successful
                // send and this point, a cancelled DisconnectAsync would throw out of the finally and the
                // job would record a failed attempt for a message the server already accepted (09.2.6-T3).
                await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // The job composes the deterministic `Message-Id`; a message without one could not be correlated
        // with its delivery record, so this refuses instead of returning an empty id.
        return providerMessageId
            ?? throw new InvalidOperationException(
                "The message carries no Message-Id, so its delivery could not be recorded (09.2.6-T3).");
    }

    /// <summary>Whether a refusal is permanent: every 5xx answer is final, every 4xx is retryable.</summary>
    /// <param name="statusCode">The SMTP status code of the refusal.</param>
    private static bool IsPermanent(SmtpStatusCode statusCode) => (int)statusCode >= 500;

    /// <summary>The MailKit socket option of one configured security mode.</summary>
    /// <param name="security">The validated configuration value.</param>
    private static SecureSocketOptions SecurityOf(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.None,
    };
}
