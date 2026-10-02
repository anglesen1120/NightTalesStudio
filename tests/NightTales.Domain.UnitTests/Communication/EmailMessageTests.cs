using NightTales.Domain.Common;
using NightTales.Domain.Communication;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Communication;

/// <summary>
/// The state transitions of the <c>email_messages</c> aggregate (09.2.6-T3, 09 §5.3, §B10 WT17):
/// the idempotency guard in the domain, the attempt counter and the <c>params</c> lifecycle that the
/// integration tests can only observe through SQL.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock, no context — QA additions on top of the task's own table.
/// </remarks>
public sealed class EmailMessageTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Queue_RaisesEmailRequested_AndStartsQueued()
    {
        var message = EmailMessage.Queue(
            Guid.CreateVersion7(),
            "password_changed",
            Language.Vi,
            new string('a', 64),
            """{"displayName":"A"}""",
            FixedNow);

        message.Status.ShouldBe(EmailStatus.Queued);
        message.Attempts.ShouldBe(0);
        message.SentAt.ShouldBeNull();
        message.ProviderMessageId.ShouldBeNull();
        message.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<EmailRequested>();
    }

    [Fact]
    public void Sending_OnSentRow_Throws()
    {
        var message = EmailMessage.Queue(
            Guid.CreateVersion7(), "password_changed", Language.Vi, new string('a', 64), "{}", FixedNow);

        message.Sending(FixedNow);
        message.Sent("<id@nighttales.app>", FixedNow);

        // 09 §5.3's idempotency guard: the job's early return is the fast path, and this throw is the
        // guard a redelivered job cannot slip past.
        Should.Throw<InvalidOperationException>(() => message.Sending(FixedNow));
    }

    [Fact]
    public void Sent_NullsParams_AndRecordsTheProviderId()
    {
        var message = EmailMessage.Queue(
            Guid.CreateVersion7(),
            "password_changed",
            Language.Vi,
            new string('a', 64),
            """{"displayName":"A","occurredAt":"t"}""",
            FixedNow);

        message.Sending(FixedNow);
        message.Sent("<id@nighttales.app>", FixedNow);

        // WT17: a row that reached `sent` keeps no render values, and the attempt error is cleared.
        message.Status.ShouldBe(EmailStatus.Sent);
        message.ParamsJson.ShouldBeNull();
        message.ProviderMessageId.ShouldBe("<id@nighttales.app>");
        message.SentAt.ShouldBe(FixedNow);
        message.LastErrorCode.ShouldBeNull();
    }

    [Fact]
    public void Failed_KeepsParams_AndClearsNothingElse()
    {
        const string paramsJson = """{"displayName":"A"}""";
        var message = EmailMessage.Queue(
            Guid.CreateVersion7(), "password_changed", Language.Vi, new string('a', 64), paramsJson, FixedNow);

        message.Sending(FixedNow);
        message.Failed("email_send_failed", FixedNow);

        // A failed attempt keeps its render values: the next attempt re-renders from the same row.
        message.Status.ShouldBe(EmailStatus.Failed);
        message.LastErrorCode.ShouldBe("email_send_failed");
        message.ParamsJson.ShouldBe(paramsJson);
        message.SentAt.ShouldBeNull();
    }

    [Fact]
    public void Sent_AfterFailedAttempt_ClearsTheErrorCode()
    {
        var message = EmailMessage.Queue(
            Guid.CreateVersion7(), "password_changed", Language.Vi, new string('a', 64), "{}", FixedNow);

        message.Sending(FixedNow);
        message.Failed("email_send_failed", FixedNow);
        message.Sending(FixedNow);
        message.Sent("<id@nighttales.app>", FixedNow);

        // The retry that succeeded: two recorded attempts, no error and no params left.
        message.Attempts.ShouldBe(2);
        message.Status.ShouldBe(EmailStatus.Sent);
        message.LastErrorCode.ShouldBeNull();
        message.ParamsJson.ShouldBeNull();
    }
}
