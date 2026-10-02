using System.Net;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Email;

/// <summary>
/// <c>SendEmailJob</c> (09 README BD9, §5.3, §B10 WT17/WT30): the idempotency guard, the send-time token,
/// the row's transitions and the two failures that must not retry.
/// </summary>
/// <remarks>
/// The job runs in-process through the Worker's own graph and the inline scheduler, so a test drives
/// "deliver the outbox row, run the job" without a Hangfire server and without sleeping.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Hangfire server")]
public sealed class SendEmailJobTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task SendEmailJob_SendsAndRecordsSent()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, clock, ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "Chủ kênh"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1);

        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("sent");
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND attempts = 1").ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND sent_at IS NOT NULL").ShouldBe(1);

        // The deterministic id of the message, so a retry is recognisably the same one (09 README BD9).
        SqlProbe.ScalarText(Database, $"SELECT provider_message_id FROM email_messages WHERE id = '{id}'")
            .ShouldBe($"<{id}@nighttales.app>");

        // WT17: a row that reached `sent` keeps no render values.
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NULL").ShouldBe(1);

        harness.Smtp.CallCount.ShouldBe(1);
        var message = harness.Smtp.LastMessage;
        message.ShouldNotBeNull();
        message.Subject.ShouldNotBeNullOrWhiteSpace();
        message.To.Mailboxes.Single().Address.ShouldBe(owner.Email);
        message.TextBody.ShouldNotBeNull().ShouldContain("Chủ kênh");

        // The html body carries every substituted value HTML-encoded (BD9), so the Latin-1 letters of a
        // Vietnamese display name travel as entities and render as the same text in a mail client.
        message.HtmlBody.ShouldNotBeNull().ShouldContain(WebUtility.HtmlEncode("Chủ kênh"));
    }

    [Fact]
    public async Task SendEmailJob_PasswordReset_MintsTokenAtSendTime()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_reset",
            Language.Vi,
            EmailHarness.Values(("displayName", "Chủ kênh"), ("expiresMinutes", "60")),
            ct);

        // Nothing token-bearing exists while the message is only queued (WT17/WT30).
        harness.Tokens.Issued.ShouldBeEmpty();
        SqlProbe.ScalarText(Database, $"SELECT coalesce(params::text, '') FROM email_messages WHERE id = '{id}'")
            .ShouldNotContain("t=");
        SqlProbe.ScalarText(Database, "SELECT coalesce(payload::text, '') FROM outbox_messages").ShouldNotContain("t=");

        (await harness.DeliverAsync(ct)).ShouldBe(1);

        harness.Tokens.Issued.ShouldBe(new[] { (owner.Id, UserActionKind.PasswordReset) });

        var message = harness.Smtp.LastMessage;
        message.ShouldNotBeNull();
        message.TextBody.ShouldNotBeNull().ShouldContain($"#t={FakeUserActionTokenService.Token}");
        message.HtmlBody.ShouldNotBeNull().ShouldContain($"#t={FakeUserActionTokenService.Token}");

        // The link lived only in the rendered body: the row keeps the token-free values and is nulled on
        // `sent`, and the outbox payload never held it either.
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NULL").ShouldBe(1);
        SqlProbe.ScalarText(Database, "SELECT coalesce(payload::text, '') FROM outbox_messages")
            .ShouldNotContain(FakeUserActionTokenService.Token);
        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("sent");
    }

    [Fact]
    public async Task SendEmailJob_AlreadySent_DoesNotSendAgain()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1);
        harness.Smtp.CallCount.ShouldBe(1);

        // A redelivered outbox message enqueues the job again; the row's `sent` guard is what makes the
        // second run a no-op (09 §5.3).
        (await harness.RunJobAgainAsync(id, ct)).ShouldBe(1);

        harness.Smtp.CallCount.ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND attempts = 1").ShouldBe(1);
        harness.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendEmailJob_TwoJobsForOneMessage_SendOnce()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        // Hold the first send inside SMTP so the second job provably starts while it is in flight.
        harness.Smtp.HoldCalls = true;

        var first = Task.Run(() => harness.RunJobOnceAsync(id, ct), ct);
        await harness.Smtp.FirstCall.WaitAsync(TimeSpan.FromSeconds(30), ct);

        // A redelivered outbox event enqueues a second job for the same message: the send lock makes it wait
        // for this attempt and then leave at the `sent` guard, so exactly one SMTP call happens
        // (09.2.6-T3 CR2). Without the lock the second job sends beside the first.
        var second = Task.Run(() => harness.RunJobOnceAsync(id, ct), ct);

        harness.Smtp.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30), ct);

        harness.Smtp.CallCount.ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("sent");
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND attempts = 1").ShouldBe(1);

        // Drain the outbox row this test queued (the row is `sent` now, so the job returns at the guard), so
        // it cannot leak into another test of the shared class database.
        (await harness.DeliverAsync(ct)).ShouldBe(1);
        harness.Smtp.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task SendEmailJob_TwoMessagesSharingATimestampPrefix_BothSend()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        var firstId = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);
        var secondId = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "B"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        // A UUIDv7 leads with its creation timestamp, so two messages queued in the same millisecond share
        // their first eight bytes. Force both ids into that shape — the one a key derived from the leading
        // bytes confuses — and drop the outbox rows the queue wrote, because the jobs are driven directly.
        var firstPrefixed = WithLeadingBytes(firstId, suffix: 0x01);
        var secondPrefixed = WithLeadingBytes(firstId, suffix: 0x02);

        await Database.ExecuteAsync(
            $"UPDATE email_messages SET id = '{firstPrefixed}' WHERE id = '{firstId}'",
            ct);
        await Database.ExecuteAsync(
            $"UPDATE email_messages SET id = '{secondPrefixed}' WHERE id = '{secondId}'",
            ct);
        await Database.ExecuteAsync($"DELETE FROM outbox_messages WHERE user_id = '{owner.Id}'", ct);

        harness.Smtp.HoldCalls = true;

        var first = Task.Run(() => harness.RunJobOnceAsync(firstPrefixed, ct), ct);
        await harness.Smtp.FirstCall.WaitAsync(TimeSpan.FromSeconds(30), ct);

        // The second message is a different send: it must take its own lock and reach SMTP while the first
        // is still in flight. Under a key built from the leading bytes both ids collapsed onto one lock, and
        // the losing message was never sent.
        var second = Task.Run(() => harness.RunJobOnceAsync(secondPrefixed, ct), ct);
        await harness.Smtp.WaitForCallsAsync(2, ct);

        harness.Smtp.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30), ct);

        harness.Smtp.CallCount.ShouldBe(2);

        foreach (var id in new[] { firstPrefixed, secondPrefixed })
        {
            SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("sent");
        }
    }

    /// <summary>Builds an id that shares <paramref name="source"/>'s first eight bytes and fixes the rest.</summary>
    /// <param name="source">The id whose leading bytes are kept.</param>
    /// <param name="suffix">The byte written into positions 8..15.</param>
    private static Guid WithLeadingBytes(Guid source, byte suffix)
    {
        var bytes = source.ToByteArray();

        for (var index = 8; index < bytes.Length; index++)
        {
            bytes[index] = suffix;
        }

        return new Guid(bytes);
    }

    [Fact]
    public async Task SendEmailJob_SmtpFailure_ThrowsAndRecordsCode()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        harness.Smtp.Failure = new InvalidOperationException("the mail server is unreachable");

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        // The failed attempt is not a completed job: InlineJobScheduler counts only the jobs that returned
        // (InlineJobSchedulerTests), so the evidence of the throw is `Failures` below.
        (await harness.DeliverAsync(ct)).ShouldBe(0);
        harness.Failures.Count.ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("failed");
        SqlProbe.ScalarText(Database, $"SELECT last_error_code FROM email_messages WHERE id = '{id}'")
            .ShouldBe(ErrorCodes.EmailSendFailed);

        // A failed attempt keeps its values: the next attempt re-renders from the same row.
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NOT NULL").ShouldBe(1);
    }

    [Fact]
    public async Task SendEmailJob_UserGone_DoesNotRetry()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        await Database.ExecuteAsync($"UPDATE users SET status = 'suspended' WHERE id = '{owner.Id}'", ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1);

        harness.Failures.ShouldBeEmpty("a gone recipient is recorded, not retried");
        harness.Smtp.CallCount.ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("failed");
        SqlProbe.ScalarText(Database, $"SELECT last_error_code FROM email_messages WHERE id = '{id}'")
            .ShouldBe(ErrorCodes.EmailUserGone);
    }

    [Fact]
    public async Task SendEmailJob_AddressNotInLogs()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1);

        var toHash = SqlProbe.ScalarText(Database, $"SELECT to_hash FROM email_messages WHERE id = '{id}'");

        owner.Email.ShouldNotBeNull();
        harness.Logs.Messages.ShouldNotBeEmpty();
        harness.Logs.Messages.ShouldAllBe(line => !line.Contains(owner.Email, StringComparison.OrdinalIgnoreCase));
        harness.Logs.Messages.ShouldContain(line => line.Contains(toHash, StringComparison.Ordinal));
    }
}
