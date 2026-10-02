using System.Diagnostics.Metrics;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Email;
using NightTales.Domain.Common;
using NightTales.Domain.Communication;
using NightTales.Infrastructure.Email;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;
namespace NightTales.Api.IntegrationTests.Email;

/// <summary>
/// The negative and boundary cases of 09.2.6-T3 that the task's own table does not cover: value-shaped
/// attacks on the composed message, a refused send the server can describe, a retry after failure, a
/// token-bearing template whose minter is not registered yet, and the misconfiguration cases of the
/// start-up gate.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Hangfire server")]
public sealed class EmailEdgeCaseTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task SendEmailJob_CrlfValue_NeverReachesAHeader()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        // A producer value that tries to smuggle a second recipient header and a markup payload. The
        // allow-list accepts any string; the renderer and MimeKit are what make it harmless.
        const string attack = "A\r\nBcc: attacker@evil.test <script>alert(1)</script>";

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", attack), ("occurredAt", "2026-01-01 00:00")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1);

        var message = harness.Smtp.LastMessage;
        message.ShouldNotBeNull();

        // The value survives verbatim (MimeKit normalises CRLF to LF when the text body is decoded),
        // is encoded in the html body, and is nowhere in the header block: the recipient list is the
        // user's address alone.
        message.TextBody.ShouldNotBeNull().ShouldContain("Bcc: attacker@evil.test <script>alert(1)</script>", Case.Insensitive);
        message.HtmlBody.ShouldNotBeNull().ShouldContain("&lt;script&gt;", Case.Insensitive);
        message.To.Mailboxes.Select(m => m.Address).ShouldBe([owner.Email!]);
        message.Headers.ShouldNotContain(header => header.Id == HeaderId.Bcc);

        // On the wire the header block ends at the first blank line; MimeKit's ToString uses the
        // platform newline (LF on macOS), so the split is on the doubled newline, and the block must
        // not carry the smuggled header name or address in any form that spells them literally.
        var wire = message.ToString();
        var headerBlock = wire[..wire.IndexOf("\n\n", StringComparison.Ordinal)];
        headerBlock.ShouldNotContain("attacker@evil.test");
        headerBlock.ShouldNotContain("Bcc:");
    }

    [Fact]
    public async Task SendEmailJob_ServerRefusal_IsFailedWithoutExceptionDetails()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        // A describable refusal (the adapter's SmtpCommandException path): an Error, not a throw. The
        // refusal text a real server returns can echo the recipient, so only the code may be recorded
        // or thrown (09 README BD16).
        var refusal = $"{owner.Email} mailbox unavailable";
        harness.Smtp.Response = _ => Error.Failure(ErrorCodes.EmailSendFailed, refusal);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(0);
        harness.Failures.Count.ShouldBe(1);
        harness.Failures[0].Message.ShouldNotContain(refusal);
        harness.Failures[0].Message.ShouldNotContain(owner.Email!);

        // IEmailDeliveryStatus is what a caller polls (BD9): the failure is readable with its code,
        // no sent instant, and nothing the server echoed (D10's filter gives the owner's scope).
        var delivery = await harness.ReadStatusAsync(owner.Id, id, ct);
        delivery.IsError.ShouldBeFalse();
        delivery.Value.Status.ShouldBe(EmailStatus.Failed);
        delivery.Value.LastErrorCode.ShouldBe(ErrorCodes.EmailSendFailed);
        delivery.Value.SentAt.ShouldBeNull();

        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("failed");
        SqlProbe.ScalarText(Database, $"SELECT last_error_code FROM email_messages WHERE id = '{id}'")
            .ShouldBe(ErrorCodes.EmailSendFailed);

        // A refusal echoes nothing about the recipient into the log, and the row keeps its render
        // values for the retry. The stored `params` hold the producer's values — never the address
        // (BD16), which this row would carry only through `to_hash`.
        harness.Logs.Messages.ShouldAllBe(line => !line.Contains(owner.Email!, StringComparison.OrdinalIgnoreCase));
        SqlProbe.ScalarText(Database, $"SELECT params::text FROM email_messages WHERE id = '{id}'")
            .ShouldNotContain(owner.Email!);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NOT NULL").ShouldBe(1);
    }

    [Fact]
    public async Task SendEmailJob_RetryAfterFailure_SendsOnceMore()
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

        // First attempt: the transport is down — the row records the failure and keeps its params.
        harness.Smtp.Failure = new InvalidOperationException("connection refused");
        (await harness.DeliverAsync(ct)).ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("failed");

        // Second attempt: the transport is back — the same row renders again and sends.
        harness.Smtp.Failure = null;
        (await harness.RunJobAgainAsync(id, ct)).ShouldBe(1);

        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("sent");
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NULL").ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT last_error_code FROM email_messages WHERE id = '{id}'").ShouldBe("");
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND attempts = 2").ShouldBe(1);
        harness.Smtp.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task SendEmailJob_Metrics_CountEachOutcome()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        // The Design's four instruments on the NightTales.Email meter: queued per template, sent per
        // template, failed per template+code, and the send-time histogram. A MeterListener scoped to
        // this test sees only this host's measurements because the meter is a singleton per host.
        var measurements = new List<(string Instrument, string Template, string? Code)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == EmailMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) =>
            {
                var tagArray = tags.ToArray();
                var template = tagArray
                    .FirstOrDefault(tag => tag.Key == EmailMetrics.TemplateTagName).Value?.ToString();
                var code = tagArray
                    .FirstOrDefault(tag => tag.Key == EmailMetrics.CodeTagName).Value?.ToString();
                measurements.Add((instrument.Name, template ?? string.Empty, code));
            });
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) =>
            {
                var tagArray = tags.ToArray();
                var template = tagArray
                    .FirstOrDefault(tag => tag.Key == EmailMetrics.TemplateTagName).Value?.ToString();
                measurements.Add((instrument.Name, template ?? string.Empty, null));
            });
        listener.Start();

        await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);
        (await harness.DeliverAsync(ct)).ShouldBe(1);

        harness.Smtp.Failure = new InvalidOperationException("connection refused");
        await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);
        (await harness.DeliverAsync(ct)).ShouldBe(0);

        measurements.ShouldContain(m =>
            m.Instrument == EmailMetrics.QueuedCounterName && m.Template == "password_changed");
        measurements.ShouldContain(m =>
            m.Instrument == EmailMetrics.SentCounterName && m.Template == "password_changed");
        measurements.ShouldContain(m =>
            m.Instrument == EmailMetrics.FailedCounterName
            && m.Template == "password_changed"
            && m.Code == ErrorCodes.EmailSendFailed);
        measurements.ShouldContain(m =>
            m.Instrument == EmailMetrics.SendSecondsName && m.Template == "password_changed");
    }

    [Fact]
    public async Task SendEmailJob_MinterFailure_FailsRowAndPropagates()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        // `email_verify` declares a minter key (`link`) but no minter is registered for it until
        // 09.3.1-T3, so the resolution throws before the SMTP call: the job must not send a half-rendered
        // message and must not mint through the wrong token store. The failed attempt is recorded before
        // the exception leaves the job (09.2.6-T3 L169), so the row says `failed` with a code and an
        // attempt instead of still looking queued, and the throw is what makes Hangfire retry it.
        var id = await harness.QueueAsync(
            owner.Id,
            "email_verify",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("expiresHours", "24")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(0);
        harness.Failures.Count.ShouldBe(1);
        harness.Failures[0].Message.ShouldContain("email_verify");
        harness.Smtp.CallCount.ShouldBe(0);
        harness.Tokens.Issued.ShouldBeEmpty();

        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("failed");
        SqlProbe.ScalarText(Database, $"SELECT last_error_code FROM email_messages WHERE id = '{id}'")
            .ShouldBe(ErrorCodes.EmailSendFailed);

        // The attempt is counted and the render values survive for the retry; neither the address nor the
        // exception's own text reaches a log line (BD16), only the code.
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND attempts = 1").ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NOT NULL").ShouldBe(1);
        harness.Logs.Messages.ShouldContain(line => line.Contains(ErrorCodes.EmailSendFailed, StringComparison.Ordinal));
        harness.Logs.Messages.ShouldAllBe(line => !line.Contains(owner.Email!, StringComparison.OrdinalIgnoreCase));
        harness.Logs.Messages.ShouldAllBe(line => !line.Contains("IEmailTokenMinter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendEmailJob_UnreadableStoredParams_FailsRowAndPropagates()
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

        // A row whose stored `params` will not read back (a JSON null, not the object the queue writes):
        // the read throws before the render, and it is the second escape hatch the job has to record —
        // the failure is on the row, counted as an attempt, and the throw still reaches Hangfire.
        await Database.ExecuteAsync($"UPDATE email_messages SET params = 'null'::jsonb WHERE id = '{id}'", ct);

        (await harness.DeliverAsync(ct)).ShouldBe(0);
        harness.Failures.Count.ShouldBe(1);
        harness.Smtp.CallCount.ShouldBe(0);

        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("failed");
        SqlProbe.ScalarText(Database, $"SELECT last_error_code FROM email_messages WHERE id = '{id}'")
            .ShouldBe(ErrorCodes.EmailSendFailed);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND attempts = 1").ShouldBe(1);
    }

    [Fact]
    public async Task SendEmailJob_PermanentRefusal_IsFailedWithoutRetry()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        // A 5xx refusal is permanent: the job fails the row and returns instead of feeding Hangfire's five
        // attempts (09.2.6-T3 L176).
        harness.Smtp.Response = _ => Error.Failure(ErrorCodes.EmailSendRejected, "550 5.1.1 mailbox unavailable");

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1);
        harness.Failures.ShouldBeEmpty("a permanent refusal is recorded, not retried");
        harness.Smtp.CallCount.ShouldBe(1);

        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("failed");
        SqlProbe.ScalarText(Database, $"SELECT last_error_code FROM email_messages WHERE id = '{id}'")
            .ShouldBe(ErrorCodes.EmailSendRejected);

        // A failed row keeps its values, and nothing the server echoed reaches a log line (BD16).
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NOT NULL").ShouldBe(1);
        harness.Logs.Messages.ShouldAllBe(line => !line.Contains(owner.Email!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SendEmailJob_PostSendCommitFailure_LeavesRecoverableRow()
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

        // The SMTP server accepts the message and only the commit that would record `sent` breaks.
        await Database.ExecuteAsync(
            "CREATE FUNCTION nts_block_sent() RETURNS trigger AS $$ BEGIN IF NEW.status = 'sent' THEN RAISE EXCEPTION 'nts test: sent write blocked'; END IF; RETURN NEW; END; $$ LANGUAGE plpgsql",
            ct);
        await Database.ExecuteAsync(
            "CREATE TRIGGER nts_block_sent BEFORE UPDATE ON email_messages FOR EACH ROW EXECUTE FUNCTION nts_block_sent()",
            ct);

        try
        {
            (await harness.DeliverAsync(ct)).ShouldBe(0);
            harness.Failures.Count.ShouldBe(1);
            harness.Smtp.CallCount.ShouldBe(1);

            // The row is not a corrupt `failed` one: `Sent` cleared its params only in memory, so what the
            // last committed write left is a queued row with its values and no `sent_at` (09.2.6-T3 CR3).
            SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("queued");
            SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NOT NULL").ShouldBe(1);
            SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND sent_at IS NULL").ShouldBe(1);
        }
        finally
        {
            await Database.ExecuteAsync("DROP TRIGGER nts_block_sent ON email_messages", ct);
            await Database.ExecuteAsync("DROP FUNCTION nts_block_sent()", ct);
        }

        // With the fault gone the same row recovers — the retry re-reads its params and sends instead of
        // failing forever on `EmailParamsJson.FromJson(null)`.
        (await harness.RunJobAgainAsync(id, ct)).ShouldBe(1);

        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("sent");
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NULL").ShouldBe(1);
    }

    [Fact]
    public async Task QueueAsync_OversizedValue_IsStoredAndSent()
    {
        var ct = TestContextToken.Value;
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, new FakeClock(), ct);

        // No size rule exists for params (the catalog is about keys, not lengths): a very large value
        // must round-trip through the jsonb column and the render, or a documented limit must refuse
        // it — a silent third outcome would be a defect.
        var huge = new string('x', 512 * 1024);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", huge), ("occurredAt", "2026-01-01 00:00")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("sent");
        harness.Smtp.LastMessage.ShouldNotBeNull().TextBody.ShouldNotBeNull().ShouldContain(huge);
    }

    [Fact]
    public void EmailOptions_InvalidPort_FailsStartup()
    {
        var exception = Should.Throw<OptionsValidationException>(() => Start(port: "0"));

        exception.Message.ShouldBe(EmailOptionsValidator.InvalidPortFailure);
        Should.Throw<OptionsValidationException>(() => Start(port: "65536"));
    }

    [Fact]
    public void EmailOptions_InvalidTimeout_FailsStartup()
    {
        var exception = Should.Throw<OptionsValidationException>(() => Start(timeout: "0"));

        exception.Message.ShouldBe(EmailOptionsValidator.InvalidTimeoutFailure);
        Should.Throw<OptionsValidationException>(() => Start(timeout: "601"));
    }

    [Fact]
    public void EmailOptions_WhitespaceFromAddress_FailsStartup()
    {
        // The blank-address short-circuit: whitespace is "missing", so only the one rule reports —
        // the mailbox-shape rule must not add a second failure.
        var exception = Should.Throw<OptionsValidationException>(
            () => Start(fromAddress: "   "));

        exception.Message.ShouldBe(EmailOptionsValidator.MissingFromAddressFailure);
        exception.Message.ShouldNotContain("usable mailbox");
    }

    [Fact]
    public void EmailOptions_LoopbackAlias_StillRefused()
    {
        // A name that resolves to loopback but is not the literal the rule accepts: the allow-list is
        // "127.0.0.1, ::1 or localhost" verbatim, so a dotted alias must not slip through.
        var exception = Should.Throw<OptionsValidationException>(
            () => Start(host: "localhost.", security: "None"));

        exception.Message.ShouldBe(EmailOptionsValidator.InsecureTransportFailure);
    }

    [Fact]
    public void EmailOptions_SmtpAlias_BindsAndLosesToEmailSection()
    {
        // 11.1.1-T2's flat `SMTP__*` keys reach a host as the `Smtp` section: they bind when
        // `Email:Smtp:*` says nothing, and an explicit `Email:Smtp:*` value wins (task file CR 9).
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Email:From:Address"] = "no-reply@nighttales.test",
            ["Email:Smtp:Host"] = "smtp.nighttales.test",
            ["Email:Smtp:Security"] = nameof(SmtpSecurity.StartTls),
            ["Email:AddressHashPepper"] = "a-test-address-pepper",
            ["App:PublicOrigin"] = "https://app.nighttales.test",
            ["Smtp:Host"] = "127.0.0.1",
            ["Smtp:Port"] = "2525",
            ["Smtp:Username"] = "alias-user",
            ["Smtp:Password"] = "alias-pass",
        });

        builder.Services.AddEmail(builder.Configuration);

        using var app = builder.Build();
        app.Start();

        var options = app.Services.GetRequiredService<IOptions<EmailOptions>>().Value;

        // Host is the explicit `Email:Smtp` value; port and credentials come from the alias.
        options.Smtp.Host.ShouldBe("smtp.nighttales.test");
        options.Smtp.Port.ShouldBe(2525);
        options.Smtp.Username.ShouldBe("alias-user");
        options.Smtp.Password.ShouldBe("alias-pass");
    }

    [Fact]
    public void EmailOptions_SmtpAliasUser_BindsUsername()
    {
        // 11.1.1-T2's `.env` exports `SMTP__USER`/`SMTP__PASSWORD`; the environment provider turns the flat
        // names into the keys `Smtp:User`/`Smtp:Password`. `User` has no matching property on SmtpOptions
        // (it is `Username`), so the alias must be mapped explicitly or the documented pair leaves Username
        // null and the host refuses to start.
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Email:From:Address"] = "no-reply@nighttales.test",
            ["Email:Smtp:Host"] = "smtp.nighttales.test",
            ["Email:Smtp:Security"] = nameof(SmtpSecurity.StartTls),
            ["Email:AddressHashPepper"] = "a-test-address-pepper",
            ["App:PublicOrigin"] = "https://app.nighttales.test",
            ["Smtp:User"] = "alias-user",
            ["Smtp:Password"] = "alias-pass",
        });

        builder.Services.AddEmail(builder.Configuration);

        using var app = builder.Build();
        app.Start();

        var options = app.Services.GetRequiredService<IOptions<EmailOptions>>().Value;

        options.Smtp.Username.ShouldBe("alias-user");
        options.Smtp.Password.ShouldBe("alias-pass");
    }

    /// <summary>Builds the e-mail composition for one misconfiguration and starts it.</summary>
    private static void Start(
        string host = "smtp.nighttales.test",
        string port = "587",
        string timeout = "30",
        string security = "StartTls",
        string fromAddress = "no-reply@nighttales.test")
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Email:From:Address"] = fromAddress,
            ["Email:Smtp:Host"] = host,
            ["Email:Smtp:Port"] = port,
            ["Email:Smtp:Security"] = security,
            ["Email:Smtp:TimeoutSeconds"] = timeout,
            ["Email:AddressHashPepper"] = "a-test-address-pepper",
            ["App:PublicOrigin"] = "https://app.nighttales.test",
        };

        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddEmail(builder.Configuration);

        using var app = builder.Build();
        app.Start();
    }
}
