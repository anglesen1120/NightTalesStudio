using ErrorOr;
using MimeKit;
using NightTales.Application.Abstractions.Email;

namespace NightTales.TestKit.Email;

/// <summary>
/// The <see cref="ISmtpEmailSender"/> every e-mail test replaces the transport with (09 README D13,
/// 09.2.6-T3): it counts the calls the job made and captures the composed messages, so a test can assert
/// what was rendered, who it went to and — for the idempotency guard — how many times the job tried.
/// </summary>
/// <remarks>
/// <para>
/// It is the seam of the whole pipeline: the renderer, the minter, the idempotency guard and the row's
/// state transitions are all exercised through the real code, and only the SMTP conversation is replaced.
/// No test needs a mail server, and <c>EmailEndToEndTests</c> is the one test that deliberately uses the
/// real adapter — against 11.1.1-T2's Mailpit sink, behind its own environment gate.
/// </para>
/// <para>
/// Register it as a singleton and remove the host's own registration first, so the count a test reads is
/// the count the job produced.
/// </para>
/// </remarks>
public sealed class FakeSmtpEmailSender : ISmtpEmailSender
{
    private readonly List<MimeMessage> _sent = [];

    /// <summary>How many times the job called the transport, whether or not the call succeeded.</summary>
    public int CallCount { get; private set; }

    /// <summary>The messages the transport accepted, in call order.</summary>
    public IReadOnlyList<MimeMessage> Sent => _sent;

    /// <summary>The last message the transport accepted, or <see langword="null"/> when it accepted none.</summary>
    public MimeMessage? LastMessage => _sent.Count == 0 ? null : _sent[^1];

    /// <summary>
    /// When set, the call throws it instead of accepting the message — the shape of an unreachable server or
    /// a TLS failure. The message must never contain the recipient address: a real adapter's exception is
    /// not logged either (09 README BD16).
    /// </summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, the call answers it instead of accepting the message — the describable refusal.</summary>
    public Func<MimeMessage, ErrorOr<string>>? Response { get; set; }

    /// <inheritdoc />
    public Task<ErrorOr<string>> SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        CallCount++;

        if (Failure is not null)
        {
            throw Failure;
        }

        if (Response is not null)
        {
            return Task.FromResult(Response(message));
        }

        _sent.Add(message);

        // The raw header, like the real adapter: the bracketed deterministic id of BD9, not the bare form
        // MailKit's `MimeMessage.MessageId` normalises to. A message without one could not be correlated
        // with its delivery record, so this fails instead of returning an id.
        return Task.FromResult<ErrorOr<string>>(
            message.Headers[HeaderId.MessageId]
            ?? throw new InvalidOperationException(
                "The message carries no Message-Id, so its delivery could not be recorded (09.2.6-T3)."));
    }
}
