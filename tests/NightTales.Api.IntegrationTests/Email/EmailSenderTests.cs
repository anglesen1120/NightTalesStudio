using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Email;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Email;

/// <summary>
/// The queueing half of 09.2.6-T3 (09 README BD9/D11, §B10 WT30): what <c>IEmailSender</c> writes, in which
/// transaction, and what it refuses.
/// </summary>
/// <remarks>
/// In the "Hangfire server" collection: the graph under test is the Worker's, which installs Hangfire's
/// schema and its process-global log provider.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Hangfire server")]
public sealed class EmailSenderTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The one outbox event this pipeline writes (09.2.6-T3).</summary>
    private static readonly string[] EmailRequestedV1 = ["email_requested.v1"];

    [Fact]
    public async Task QueueAsync_WritesOutboxRowTypeEmailRequestedV1()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "Chủ kênh"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        SqlProbe.QueryStrings(Database, "SELECT type FROM outbox_messages")
            .ShouldBe(EmailRequestedV1);

        var payload = SqlProbe.ScalarText(Database, "SELECT payload::text FROM outbox_messages");
        payload.ShouldContain(id.ToString());
        payload.ShouldContain("displayName");
        payload.ShouldNotContain("link");

        // The row keeps the allow-listed values and the hash — never the address (09 README BD16/WT30).
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}'").ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("queued");
        SqlProbe.ScalarText(Database, $"SELECT locale FROM email_messages WHERE id = '{id}'").ShouldBe("vi");
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NOT NULL").ShouldBe(1);

        var toHash = SqlProbe.ScalarText(Database, $"SELECT to_hash FROM email_messages WHERE id = '{id}'");
        toHash.ShouldNotContain("@");
        owner.Email.ShouldNotBeNull();
        toHash.ShouldNotBe(owner.Email);
        SqlProbe.QueryStrings(Database, "SELECT to_hash FROM email_messages").ShouldNotContain(owner.Email);
    }

    [Fact]
    public async Task QueueAsync_RolledBackUnitOfWork_LeavesNeitherRowNorEvent()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        using var scope = harness.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(owner.Id);

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(ct);

        var queued = await scope
            .ServiceProvider
            .GetRequiredService<IEmailSender>()
            .QueueAsync(
                new EmailMessage(owner.Id, "password_changed", Language.Vi, EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00"))),
                ct);
        queued.IsError.ShouldBeFalse();

        await unitOfWork.RollbackAsync(ct);

        // 09 README D11: the row and its `email_requested.v1` message are one transaction, so neither exists.
        // Scoped to this test's user: the class database is shared by the class's other tests.
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{owner.Id}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE user_id = '{owner.Id}'").ShouldBe(0);
    }

    [Fact]
    public async Task QueueAsync_UnknownParamKey_IsValidationError()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        var queued = await harness.TryQueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00"), ("nope", "x")),
            ct);

        queued.IsError.ShouldBeTrue();
        queued.FirstError.Code.ShouldBe(ErrorCodes.EmailTemplateKeys);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{owner.Id}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE user_id = '{owner.Id}'").ShouldBe(0);
    }

    [Fact]
    public async Task QueueAsync_PasswordReset_LinkIsNotAllowListed()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        var withLink = await harness.TryQueueAsync(
            owner.Id,
            "password_reset",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("expiresMinutes", "60"), ("link", "https://app.nighttales.test/reset-password#t=smuggled")),
            ct);

        withLink.IsError.ShouldBeTrue();
        withLink.FirstError.Code.ShouldBe(ErrorCodes.EmailTemplateKeys);

        var unknownTemplate = await harness.TryQueueAsync(
            owner.Id,
            "not_a_template",
            Language.Vi,
            EmailHarness.Values(("displayName", "A")),
            ct);

        unknownTemplate.IsError.ShouldBeTrue();
        unknownTemplate.FirstError.Code.ShouldBe(ErrorCodes.EmailTemplateUnknown);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{owner.Id}'").ShouldBe(0);
    }

    [Fact]
    public async Task QueueAsync_MinterBearingUrlKey_IsNotAllowListed()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        // Every link that carries a single-use token is minted at send time and may never be queued, so it
        // is a minter key and not a producer key (09 README §B10 WT17/WT30, 09.2.6-T3 Design).
        (string Template, string Key)[] mintedLinks =
        [
            ("owner_action_pending", "approveUrl"),
            ("setting_change_pending", "cancelUrl"),
            ("mfa_reset_forced", "resetUrl"),
            ("approval_cancel", "cancelLink"),
        ];

        foreach (var (template, key) in mintedLinks)
        {
            var definition = EmailTemplateCatalog.Find(template)
                ?? throw new InvalidOperationException($"'{template}' is not in the catalog.");
            definition.MinterKeys.ShouldContain(key);
            definition.ProducerKeys.ShouldNotContain(key);

            var queued = await harness.TryQueueAsync(
                owner.Id,
                template,
                Language.Vi,
                EmailHarness.Values((key, "https://app.nighttales.test/x#t=smuggled")),
                ct);

            queued.IsError.ShouldBeTrue(
                $"a producer must not be able to queue the minted key '{key}' of '{template}'");
            queued.FirstError.Code.ShouldBe(ErrorCodes.EmailTemplateKeys);
        }

        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{owner.Id}'").ShouldBe(0);
    }

    [Fact]
    public async Task QueueAsync_UnknownUser_IsNotFound()
    {
        var ct = TestContextToken.Value;
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        var queued = await harness.TryQueueAsync(
            Guid.CreateVersion7(),
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        queued.IsError.ShouldBeTrue();
        queued.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
    }
}
