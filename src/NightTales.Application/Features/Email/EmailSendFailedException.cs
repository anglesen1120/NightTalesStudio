namespace NightTales.Application.Features.Email;

/// <summary>
/// The exception <c>SendEmailJob</c> rethrows after it recorded a failed attempt (09.2.6-T3).
/// </summary>
/// <remarks>
/// <para>
/// Its message carries the failure code, the template name and the message id — never the exception the
/// send actually raised. A refusal a mail server produces echoes the recipient address, and the exception
/// this job throws is persisted by Hangfire in the job's state history and shown on the dashboard, so
/// rethrowing the original would undo the "log the type only" control of 09 README BD16 (09.2.6-T3).
/// </para>
/// <para>
/// Hangfire retries a job whenever it throws, so replacing the original exception does not change the
/// five-attempt policy of the <c>email</c> queue; the original type is still written to the Worker log by
/// the job's own <c>email.send.failed</c> line.
/// </para>
/// </remarks>
public sealed class EmailSendFailedException : Exception
{
    /// <summary>Builds the failure a job rethrows after it recorded the attempt.</summary>
    /// <param name="code">The wire code the row was failed with (09 README D17).</param>
    /// <param name="template">The catalog template of the message, for the operator reading Hangfire.</param>
    /// <param name="emailMessageId">The <c>email_messages</c> row the attempt belonged to.</param>
    public EmailSendFailedException(string code, string template, Guid emailMessageId)
        : base($"The '{template}' e-mail '{emailMessageId}' failed with '{code}' and will be retried (09.2.6-T3).")
    {
        Code = code;
        Template = template;
        EmailMessageId = emailMessageId;
    }

    /// <summary>The wire code the row was failed with.</summary>
    public string Code { get; }

    /// <summary>The catalog template of the message.</summary>
    public string Template { get; }

    /// <summary>The <c>email_messages</c> row the attempt belonged to.</summary>
    public Guid EmailMessageId { get; }
}
