using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Email.TokenMinters;
using NightTales.Domain.Common;
using NightTales.Domain.Communication;
using DomainEmailMessage = NightTales.Domain.Communication.EmailMessage;

namespace NightTales.Application.Features.Email.Jobs;

/// <summary>
/// Sends one queued e-mail (09 README BD9, 09.2.6-T3): renders the <c>vi</c>/<c>en</c> template pair,
/// mints the template's token values, hands the message to SMTP and records the outcome in
/// <c>email_messages</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The message id is the idempotency guard</b> (09 §5.3): the row is loaded again on every attempt and
/// a row that already reached <see cref="EmailStatus.Sent"/> returns without a second SMTP call, so two
/// Workers that race the same Hangfire job can never deliver twice. The <c>Message-Id</c> is composed from
/// that id (<c>&lt;id@nighttales.app&gt;</c>) for the same reason: a retry is recognisably the same
/// message, not a second one.
/// </para>
/// <para>
/// <b>The scope is pinned before any context exists</b> (09 README D10). The job cannot inject
/// <see cref="IAppDbContext"/>, because the tenant filter fixes its user when the context is constructed
/// and <see cref="ISystemContext.For"/> refuses to run after that; it resolves the owner through
/// <see cref="ISystemWorkQuery"/>, calls <c>For(userId)</c> and only then resolves the context and the
/// other services from its own scope. That is why the dependencies below are resolved by hand.
/// </para>
/// <para>
/// <b>Failures are recorded, then rethrown</b> so Hangfire applies the five attempts of the <c>email</c>
/// queue policy. Every exception raised once the attempt has been recorded on the row is written to the row
/// first — with the attempt's own code when it has one and <c>email_send_failed</c> when it does not, so a
/// failure before the SMTP call (a minter that throws, an unparsable stored <c>params</c>) can never leave
/// the row looking queued. A recipient that no longer exists is the one case that does not retry: the row is
/// failed with <c>email_user_gone</c> and the job returns. Nothing about the address is ever logged — the
/// hash is (09 README BD16) — and a failed attempt is logged by exception <em>type</em> only, because a
/// server's refusal message echoes the recipient.
/// </para>
/// <para>
/// It calls <c>SaveChangesAsync</c> itself: 09 README D5's "handlers never save" rule is about Mediator
/// handlers inside the unit-of-work pipeline, and a job owns its own scope.
/// </para>
/// </remarks>
/// <param name="services">The job's own DI scope, from which every other dependency is resolved once the scope is pinned.</param>
/// <param name="clock">The attempt and acceptance instants.</param>
/// <param name="metrics">The sent/failed counters and the send histogram.</param>
/// <param name="logger">The job's log; it carries ids and the address hash only.</param>
[JobPolicy(Queue = JobQueue.Email, Retries = 5)]
public sealed partial class SendEmailJob(
    IServiceProvider services,
    IClock clock,
    EmailMetrics metrics,
    ILogger<SendEmailJob> logger) : IBackgroundJob
{
    /// <summary>The domain of the deterministic <c>Message-Id</c>; its local part is the message id.</summary>
    public const string MessageIdDomain = "nighttales.app";

    /// <inheritdoc />
    public async Task RunAsync(Guid emailMessageId, CancellationToken cancellationToken)
    {
        var owner = await FindOwnerAsync(emailMessageId, cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            LogMessageMissing(emailMessageId);

            return;
        }

        // 09 README D10: pin first, resolve after — a context created before this call would capture no
        // user and read nothing, and the ordering guard refuses the late call instead of doing that.
        using var pinned = services.GetRequiredService<ISystemContext>().For(owner.Value);

        var database = services.GetRequiredService<IAppDbContext>();
        var row = await database
            .EmailMessages
            .FirstOrDefaultAsync(message => message.Id == emailMessageId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            LogMessageNotVisible(emailMessageId);

            return;
        }

        if (row.Status == EmailStatus.Sent)
        {
            // 09 §5.3: the guard that makes a redelivered outbox message or a second Hangfire attempt free.
            LogAlreadySent(row.Id, row.ToHash);

            return;
        }

        row.Sending(clock.UtcNow);

        // Every failure of the attempt is recorded on the row before the exception leaves the job: Hangfire
        // then retries against a row that says `failed` with the code and the attempt count (09.2.6-T3 L169),
        // never one that still looks queued. An exception this job cannot classify — a token-bearing template
        // whose minter is not registered yet, a stored `params` that will not parse, a database error — is a
        // failed attempt too, and it wears the send-failure code because the catalogue has no closer one.
        var failureCode = ErrorCodes.EmailSendFailed;
        var elapsed = TimeSpan.Zero;
        string providerMessageId;

        try
        {
            var recipient = await services
                .GetRequiredService<IEmailRecipientQuery>()
                .GetAsync(row.UserId, cancellationToken)
                .ConfigureAwait(false);

            if (recipient.IsError
                || recipient.Value.Status != UserStatus.Active
                || string.IsNullOrWhiteSpace(recipient.Value.Address))
            {
                LogRecipientGone(ErrorCodes.EmailUserGone, row.Id, row.ToHash);

                await FailAsync(database, row, ErrorCodes.EmailUserGone, cancellationToken).ConfigureAwait(false);

                return;
            }

            var parameters = EmailParamsJson.FromJson(row.ParamsJson!);
            var minted = await services
                .GetRequiredService<IEmailTokenMinters>()
                .MintAsync(row.UserId, row.Template, parameters, cancellationToken)
                .ConfigureAwait(false);

            if (minted.IsError)
            {
                failureCode = minted.FirstError.Code;

                throw new InvalidOperationException(
                    $"Minting the '{row.Template}' values of e-mail '{row.Id}' failed with '{failureCode}'.");
            }

            // The attempt counter and the single-use token row commit together, before the SMTP call: a token
            // that exists but was never mailed is invalidated by the next attempt's mint, while a mail that
            // carried a token the database does not know would be unusable (09 README §B10 WT17).
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var values = Merge(parameters, minted.Value);
            var rendered = services
                .GetRequiredService<IEmailRenderer>()
                .Render(row.Template, row.Locale, values);

            var message = Compose(
                row.Id,
                rendered,
                recipient.Value.Address,
                services.GetRequiredService<IEmailFromAddress>().From,
                clock.UtcNow);

            var started = Stopwatch.GetTimestamp();
            var sent = await services
                .GetRequiredService<ISmtpEmailSender>()
                .SendAsync(message, cancellationToken)
                .ConfigureAwait(false);
            elapsed = Stopwatch.GetElapsedTime(started);

            if (sent.IsError)
            {
                failureCode = sent.FirstError.Code;

                throw new InvalidOperationException(
                    $"Sending the '{row.Template}' e-mail '{row.Id}' failed with '{failureCode}'.");
            }

            providerMessageId = sent.Value;
            row.Sent(providerMessageId, clock.UtcNow);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailAsync(database, row, failureCode, cancellationToken).ConfigureAwait(false);

            LogSendFailed(failureCode, row.Id, row.Template, row.ToHash, exception.GetType().Name);

            throw;
        }

        metrics.Sent(row.Template, elapsed);
        LogSent(row.Id, row.Template, row.ToHash, providerMessageId);
    }

    /// <summary>
    /// Reads the <c>(id, user_id)</c> pair of one message through the single allow-listed cross-user read
    /// (09 README D10). Only ids cross the tenant boundary here; every row read after this goes through the
    /// filter of the pinned scope.
    /// </summary>
    /// <param name="emailMessageId">The message the job was enqueued for.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    private async Task<Guid?> FindOwnerAsync(Guid emailMessageId, CancellationToken cancellationToken)
    {
        // The pair is read in a scope of its own (09 README D10, `ISystemWorkQuery`): the read model is a
        // DbContext, and the ordering guard of `ISystemContext.For` refuses to pin a scope that already
        // created one, so this lookup must not run in the very scope the job is about to pin.
        using var lookup = services.GetRequiredService<IServiceScopeFactory>().CreateScope();

        var systemWork = lookup.ServiceProvider.GetRequiredService<ISystemWorkQuery>();

        await foreach (var pair in systemWork
            .PendingAsync<DomainEmailMessage>(message => message.Id == emailMessageId, 1, cancellationToken)
            .ConfigureAwait(false))
        {
            return pair.UserId;
        }

        return null;
    }

    /// <summary>
    /// Records a failed attempt: the code on the row, the failure counter and the commit — the row stays
    /// readable with its <c>params</c> so the next attempt (or an operator, 09.2.6-T2) can try again.
    /// </summary>
    /// <param name="database">The job's write model.</param>
    /// <param name="row">The message being attempted.</param>
    /// <param name="code">The failure code (<c>email_user_gone</c>, <c>email_send_failed</c>, …).</param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    private async Task FailAsync(
        IAppDbContext database,
        DomainEmailMessage row,
        string code,
        CancellationToken cancellationToken)
    {
        metrics.Failed(row.Template, code);
        row.Failed(code, clock.UtcNow);

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The composed message: the deterministic id, the accepted sender, the current address.</summary>
    /// <param name="emailMessageId">The row id the <c>Message-Id</c> is built from.</param>
    /// <param name="rendered">The rendered subject and bodies.</param>
    /// <param name="address">The address read now, never the value the message was queued with.</param>
    /// <param name="from">The platform's sender mailbox.</param>
    /// <param name="now">The composition instant, written into the <c>Date</c> header.</param>
    private static MimeMessage Compose(
        Guid emailMessageId,
        RenderedEmail rendered,
        string address,
        MailboxAddress from,
        DateTimeOffset now)
    {
        if (!MailboxAddress.TryParse(address, out var recipient))
        {
            // The address is the one stored column of the account; an unparseable value is a data bug an
            // operator has to fix. It is not logged: the fix needs the account, not the value in a log line.
            throw new InvalidOperationException("The stored recipient address is not a usable mailbox.");
        }

        var body = new BodyBuilder
        {
            TextBody = rendered.TextBody,
            HtmlBody = rendered.HtmlBody,
        };

        var message = new MimeMessage
        {
            MessageId = string.Concat("<", emailMessageId.ToString("D"), "@", MessageIdDomain, ">"),
            Subject = rendered.Subject,
            Date = now,
        };

        message.From.Add(from);
        message.To.Add(recipient);
        message.Body = body.ToMessageBody();

        return message;
    }

    /// <summary>The producer's queued values plus the values the minter produced at send time.</summary>
    /// <param name="parameters">The stored, allow-listed values.</param>
    /// <param name="minted">The send-time values; empty for a template without a minter.</param>
    private static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> minted)
    {
        var values = new Dictionary<string, string>(parameters, StringComparer.Ordinal);

        foreach (var pair in minted)
        {
            values[pair.Key] = pair.Value;
        }

        return values;
    }

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Warning,
        Message = "email.message.missing message={EmailMessageId}: no email_messages row owns it, so nothing is sent")]
    private partial void LogMessageMissing(Guid emailMessageId);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Warning,
        Message = "email.message.missing message={EmailMessageId}: the row is not readable in its owner's scope, so nothing is sent")]
    private partial void LogMessageNotVisible(Guid emailMessageId);

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Debug,
        Message = "email.message.already-sent message={EmailMessageId} toHash={ToHash}: nothing to do")]
    private partial void LogAlreadySent(Guid emailMessageId, string toHash);

    [LoggerMessage(
        EventId = 1303,
        Level = LogLevel.Warning,
        Message = "{Code} message={EmailMessageId} toHash={ToHash}: the account is not mailable, so the message is failed without an attempt")]
    private partial void LogRecipientGone(string code, Guid emailMessageId, string toHash);

    [LoggerMessage(
        EventId = 1304,
        Level = LogLevel.Error,
        Message = "{Code} message={EmailMessageId} template={Template} toHash={ToHash} exception={ExceptionType}")]
    private partial void LogSendFailed(string code, Guid emailMessageId, string template, string toHash, string exceptionType);

    [LoggerMessage(
        EventId = 1305,
        Level = LogLevel.Information,
        Message = "email.sent message={EmailMessageId} template={Template} toHash={ToHash} providerMessageId={ProviderMessageId}")]
    private partial void LogSent(Guid emailMessageId, string template, string toHash, string providerMessageId);
}
