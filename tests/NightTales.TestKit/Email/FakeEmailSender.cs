using ErrorOr;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using DomainEmailMessage = NightTales.Domain.Communication.EmailMessage;

namespace NightTales.TestKit.Email;

/// <summary>
/// The switchboard and call log of <see cref="FakeEmailSender"/>, held by the test and shared by every
/// scope of the host.
/// </summary>
public sealed class FakeEmailSenderState
{
    private readonly List<EmailMessage> _calls = [];
    private readonly object _gate = new();

    /// <summary>
    /// When true the next <c>QueueAsync</c> answers <c>service_unavailable</c> instead of writing its row,
    /// which is how a test drives a failure <em>after</em> the state was claimed.
    /// </summary>
    public bool Fail { get; set; }

    /// <summary>
    /// When true — and with <see cref="Fail"/> on — the sender writes its real row and outbox event
    /// <em>before</em> it answers the failure, the shape a sender that failed after its own write has. That
    /// is what turns the "queue failure leaves no e-mail row" assertion into evidence about a rolled-back row
    /// rather than about a row nothing ever wrote (R2).
    /// </summary>
    public bool WriteBeforeFailing { get; set; }

    /// <summary>Every queue request the sender received, in arrival order.</summary>
    public IReadOnlyList<EmailMessage> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>How many queue requests arrived.</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    internal void Record(EmailMessage message)
    {
        lock (_gate)
        {
            _calls.Add(message);
        }
    }
}

/// <summary>
/// The hand-written <see cref="IEmailSender"/> for the connect tests: it writes a real
/// <c>email_messages</c> row into the ambient unit of work — and raises its outbox event — so a rollback
/// assertion ("no e-mail row, no outbox row") is evidence about the transaction, not about a list — and
/// it can answer a failure instead, so a test can fail the queue after the callback claimed its state.
/// </summary>
/// <remarks>
/// Two differences from the production sender are deliberate: no template-catalog check (06.1.2-T2 owns
/// the connect template and it does not exist yet, and a test must not pass or fail on that), and no
/// recipient lookup (the address hash is a canary). Everything else — the row, its state and the
/// <c>email_requested.v1</c> event — goes through the domain aggregate, like the real one. With
/// <see cref="FakeEmailSenderState.WriteBeforeFailing"/> it also writes that row before it answers its
/// failure, so the rollback assertions are about rows that existed (R2).
/// </remarks>
/// <param name="state">The test's switchboard and call log.</param>
/// <param name="database">The ambient write model: the row must commit with the caller's unit.</param>
/// <param name="clock">The host's clock, so the row's <c>created_at</c> is the test's instant.</param>
public sealed class FakeEmailSender(FakeEmailSenderState state, AppDbContext database, IClock clock) : IEmailSender
{
    private const string ToHashPlaceholder = "canary-to-hash-4f2a4f2a4f2a4f2a4f2a4f2a4f2a4f2a4f2a4f2a4f2a4f2a";

    /// <inheritdoc />
    public Task<ErrorOr<Guid>> QueueAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        state.Record(message);

        if (!state.Fail)
        {
            var row = QueueRow(message);

            return Task.FromResult<ErrorOr<Guid>>(row.Id);
        }

        if (state.WriteBeforeFailing)
        {
            // The failure comes after the write: whether the row survives is exactly the question the unit
            // of work's rollback answers (R2).
            _ = QueueRow(message);
        }

        return Task.FromResult<ErrorOr<Guid>>(Error.Unexpected(ErrorCodes.ServiceUnavailable));
    }

    /// <summary>Writes the real row — and raises its outbox event — through the domain aggregate.</summary>
    /// <param name="message">The queue request.</param>
    /// <returns>The tracked row.</returns>
    private DomainEmailMessage QueueRow(EmailMessage message)
    {
        var row = DomainEmailMessage.Queue(
            message.UserId,
            message.Template,
            message.Locale,
            ToHashPlaceholder,
            "{}",
            clock.UtcNow);

        database.EmailMessages.Add(row);

        return row;
    }
}
