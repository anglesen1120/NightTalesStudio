using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Features.Email.Jobs;
using NightTales.Domain.Common;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Email;

/// <summary>
/// Runs the e-mail pipeline of 09.2.6-T3 through the real Worker composition root (09 README D13): the
/// queueing side (<c>IEmailSender</c>), the outbox dispatcher, the <c>email_requested.v1</c> handler and
/// <c>SendEmailJob</c> — with the SMTP conversation and the token store replaced by hand-written fakes.
/// </summary>
/// <remarks>
/// <para>
/// Every host here is a <see cref="WorkerHost"/>, so the graph is the production one, and no test needs a
/// mail server: <see cref="FakeSmtpEmailSender"/> is the transport, and <c>EmailEndToEndTests</c> is the one
/// test that deliberately keeps the real adapter.
/// </para>
/// <para>
/// The queueing helpers pin the scope to the message's owner before resolving the context, exactly as a
/// command handler runs (09 README D10), and commit with <c>SaveChangesAsync</c>, which is what writes the
/// outbox row of the same transaction (D11).
/// </para>
/// </remarks>
internal sealed class EmailHarness : IAsyncDisposable
{
    private readonly WorkerHost _host;

    private EmailHarness(
        WorkerHost host,
        FakeSmtpEmailSender smtp,
        FakeUserActionTokenService tokens,
        CapturingEmailLogs logs)
    {
        _host = host;
        Smtp = smtp;
        Tokens = tokens;
        Logs = logs;
    }

    /// <summary>The transport the job calls; it counts the calls and captures the messages.</summary>
    internal FakeSmtpEmailSender Smtp { get; }

    /// <summary>The token store behind <c>IUserActionTokenService</c> (09.3.3-T1 owns the real one).</summary>
    internal FakeUserActionTokenService Tokens { get; }

    /// <summary>Every log line of the host, whatever its category.</summary>
    internal CapturingEmailLogs Logs { get; }

    /// <summary>The inline scheduler's failures, so a test can prove a job threw (and will be retried).</summary>
    internal IReadOnlyList<Exception> Failures => _host.Jobs.Failures;

    /// <summary>Starts the Worker graph with the fakes registered.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="clock">The controlled clock.</param>
    /// <param name="cancellationToken">Cancels the host start.</param>
    /// <param name="configure">Extra host configuration (settings, extra services).</param>
    /// <param name="fakeTransport">
    /// <see langword="true"/> replaces <c>ISmtpEmailSender</c> with <see cref="FakeSmtpEmailSender"/>;
    /// <see langword="false"/> keeps the real MailKit adapter for <c>EmailEndToEndTests</c>.
    /// </param>
    internal static async Task<EmailHarness> StartAsync(
        TestDatabase database,
        FakeClock clock,
        CancellationToken cancellationToken,
        Action<WorkerHost>? configure = null,
        bool fakeTransport = true)
    {
        var smtp = new FakeSmtpEmailSender();
        var tokens = new FakeUserActionTokenService();
        var logs = new CapturingEmailLogs();

        var host = new WorkerHost(database, clock)
            .ConfigureServices(services =>
            {
                if (fakeTransport)
                {
                    services.RemoveAll<ISmtpEmailSender>();
                    services.AddSingleton<ISmtpEmailSender>(smtp);
                }

                services.RemoveAll<IUserActionTokenService>();
                services.AddSingleton<IUserActionTokenService>(tokens);

                services.AddLogging(builder => builder.AddProvider(logs));
            });

        configure?.Invoke(host);

        await host.StartAsync(cancellationToken);

        return new EmailHarness(host, smtp, tokens, logs);
    }

    /// <summary>The allow-listed values of one message, spelled inline by the tests.</summary>
    /// <param name="pairs">Key/value pairs, in the order the test wrote them.</param>
    internal static IReadOnlyDictionary<string, string> Values(params (string Key, string Value)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        return pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>A scope over the started host, pinned to nobody; the caller pins it or resolves ports.</summary>
    internal IServiceScope CreateScope() => _host.CreateScope();

    /// <summary>Queues one message as its owner and commits, the way a command handler does.</summary>
    /// <param name="userId">The owner and recipient.</param>
    /// <param name="template">A catalog template name.</param>
    /// <param name="locale">The render locale.</param>
    /// <param name="values">The producer's allow-listed values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    internal async Task<Guid> QueueAsync(
        Guid userId,
        string template,
        Language locale,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var queued = await TryQueueAsync(userId, template, locale, values, cancellationToken);

        queued.IsError.ShouldBeFalse(
            $"queueing '{template}' must succeed: {string.Join(", ", queued.Errors.Select(error => error.Code))}");

        return queued.Value;
    }

    /// <summary>Queues one message and answers whatever the sender answered, without committing on error.</summary>
    /// <param name="userId">The owner and recipient.</param>
    /// <param name="template">A catalog template name.</param>
    /// <param name="locale">The render locale.</param>
    /// <param name="values">The values the caller passes, allow-listed or not.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    internal async Task<ErrorOr<Guid>> TryQueueAsync(
        Guid userId,
        string template,
        Language locale,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        using var scope = CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);

        var queued = await scope
            .ServiceProvider
            .GetRequiredService<IEmailSender>()
            .QueueAsync(
                new EmailMessage(userId, template, locale, values),
                cancellationToken);

        if (queued.IsError)
        {
            return queued.Errors;
        }

        await scope.ServiceProvider.GetRequiredService<IAppDbContext>().SaveChangesAsync(cancellationToken);

        return queued.Value;
    }

    /// <summary>
    /// Delivers one outbox batch only. The job it enqueues is left to the host's real Hangfire server
    /// (<c>WithHangfireServer</c>), so a test can observe the delivery and the job as two separate spans.
    /// </summary>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    /// <returns>How many messages the batch claimed.</returns>
    internal Task<int> RunOutboxAsync(CancellationToken cancellationToken) => _host.RunOutboxOnceAsync(cancellationToken);

    /// <summary>Delivers one outbox batch (which enqueues the job) and runs the pending job.</summary>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    /// <returns>How many jobs ran.</returns>
    internal async Task<int> DeliverAsync(CancellationToken cancellationToken)
    {
        await _host.RunOutboxOnceAsync(cancellationToken);

        return await _host.Jobs.RunPendingAsync(cancellationToken);
    }

    /// <summary>Re-enqueues one job the way a redelivered outbox message would, and runs it.</summary>
    /// <param name="emailMessageId">The message to send again.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>How many jobs ran.</returns>
    internal async Task<int> RunJobAgainAsync(Guid emailMessageId, CancellationToken cancellationToken)
    {
        using (var scope = CreateScope())
        {
            scope
                .ServiceProvider
                .GetRequiredService<IJobScheduler>()
                .Enqueue<SendEmailJob>((job, token) => job.RunAsync(emailMessageId, token));
        }

        return await _host.Jobs.RunPendingAsync(cancellationToken);
    }

    /// <summary>
    /// Runs one <c>SendEmailJob</c> for a message in a fresh DI scope, the way Hangfire activates a job —
    /// the seam a concurrency test drives two attempts of the same message through (09.2.6-T3 CR2).
    /// </summary>
    /// <param name="emailMessageId">The message to send.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    internal async Task RunJobOnceAsync(Guid emailMessageId, CancellationToken cancellationToken)
    {
        using var scope = CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<SendEmailJob>();

        await job.RunAsync(emailMessageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the delivery state as <paramref name="userId"/>; the tenant filter applies.</summary>
    /// <param name="userId">The caller's identity.</param>
    /// <param name="emailMessageId">The message to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal async Task<ErrorOr<EmailDelivery>> ReadStatusAsync(
        Guid userId,
        Guid emailMessageId,
        CancellationToken cancellationToken)
    {
        using var scope = CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);

        return await scope
            .ServiceProvider
            .GetRequiredService<IEmailDeliveryStatus>()
            .GetAsync(emailMessageId, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}

/// <summary>
/// The <c>user_action_tokens</c> store of a test (09.2.6-T3's send-time minter seam): it records every issue
/// and answers a fixed plaintext token, so a test can prove the token is created <em>when the job runs</em>
/// and never travels through the queued message.
/// </summary>
/// <remarks>
/// 09.3.3-T1 owns the real <c>EfUserActionTokenService</c> and its own version of the "minted at send time"
/// test, against the real table; this fake is what this task's pipeline can be driven with until then.
/// </remarks>
internal sealed class FakeUserActionTokenService : IUserActionTokenService
{
    /// <summary>The plaintext token every issue answers.</summary>
    public const string Token = "test-reset-token";

    private readonly List<(Guid UserId, UserActionKind Kind)> _issued = [];

    /// <summary>Every issue, in order.</summary>
    internal IReadOnlyList<(Guid UserId, UserActionKind Kind)> Issued => _issued;

    /// <inheritdoc />
    public Task<ErrorOr<string>> IssueAsync(
        Guid userId,
        UserActionKind kind,
        RequestInfo info,
        CancellationToken cancellationToken)
    {
        _issued.Add((userId, kind));

        return Task.FromResult<ErrorOr<string>>(Token);
    }

    /// <inheritdoc />
    public Task<ErrorOr<string>> IssueAsync(
        Guid userId,
        UserActionKind kind,
        Guid subjectId,
        DateTimeOffset expiresAt,
        RequestInfo info,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The e-mail pipeline issues no subject-scoped token; that member belongs to 06.2.2-T2's consumer.");

    /// <inheritdoc />
    public Task<ErrorOr<ConsumedActionToken>> ConsumeAsync(string token, UserActionKind kind, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The e-mail pipeline never consumes a token; consuming belongs to 09.3.3-T1's own tests.");
}

/// <summary>
/// Captures every log line of a test host (09 README BD16): the e-mail tests read it to prove that an
/// address never reaches a log while the address hash does.
/// </summary>
internal sealed class CapturingEmailLogs : ILoggerProvider
{
    private readonly List<string> _messages = [];

    /// <summary>Every rendered log message, in order, whatever the category.</summary>
    internal IReadOnlyList<string> Messages => _messages;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);

        return new Capture(_messages);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class Capture(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            messages.Add(formatter(state, exception));
        }
    }
}
