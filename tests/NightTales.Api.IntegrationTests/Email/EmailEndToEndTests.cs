using System.Net.Http.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Email;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Email;

/// <summary>
/// The one test that sends through the real MailKit adapter and a real SMTP server: 11.1.1-T2's Mailpit
/// sink, whose UI and API sit at <c>127.0.0.1:8025</c>.
/// </summary>
/// <remarks>
/// <para>
/// It is gated on <c>NTS_MAILPIT_URL</c> (the API base URL, which the developer exports — <c>up.sh</c>
/// starts the sink but sets no variable) and carries the <c>Local</c> category: with no Mailpit the test
/// skips instead of failing the integration job, and the pipeline's own behaviour is proven by
/// <see cref="SendEmailJobTests"/> against the fake transport.
/// </para>
/// <para>
/// It is the end-to-end proof of the vi locale: the body that arrives is the Vietnamese one, rendered from
/// the same row the job read, and it carries the reset link the minter produced at send time.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Local")]
[Collection("Hangfire server")]
public sealed class EmailEndToEndTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The variable that turns this test on: Mailpit's API base URL.</summary>
    public const string MailpitUrlVariable = "NTS_MAILPIT_URL";

    /// <summary>The SMTP port of the sink when the environment does not name one.</summary>
    private const string DefaultSmtpPort = "1025";

    [Fact]
    public async Task ResetEmail_ArrivesInMailpit()
    {
        var ct = TestContextToken.Value;
        var mailpit = MailpitUrl();
        var smtpHost = new Uri(mailpit).Host;
        var smtpPort = Environment.GetEnvironmentVariable("NTS_SMTP_PORT") ?? DefaultSmtpPort;

        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(
            Database,
            new FakeClock(),
            ct,
            host => host
                .WithSetting(EmailTestSettings.SmtpHostKey, smtpHost)
                .WithSetting(EmailTestSettings.SmtpPortKey, smtpPort)
                .WithSetting(EmailTestSettings.SmtpSecurityKey, nameof(SmtpSecurity.StartTlsWhenAvailable)),
            fakeTransport: false);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_reset",
            Language.Vi,
            EmailHarness.Values(("displayName", "Chủ kênh"), ("expiresMinutes", "60")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1);

        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("sent");

        var received = await FindDeliveredMessageAsync(mailpit, owner.Email!, ct);
        received.ShouldNotBeNull($"the reset mail of '{id}' must be readable in Mailpit at {mailpit}");

        var detail = await ReadMessageAsync(mailpit, received.Id, ct);
        detail.ShouldNotBeNull();
        detail.Text.ShouldContain("Xin chào");
        detail.Text.ShouldContain($"#t={FakeUserActionTokenService.Token}");
        detail.Text.ShouldNotContain("Hello");
    }

    /// <summary>The Mailpit API base URL, or a skip when the sink is not part of this run.</summary>
    private static string MailpitUrl()
    {
        var url = Environment.GetEnvironmentVariable(MailpitUrlVariable);

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(url),
            $"Mailpit is not part of this run: 11.1.1-T2's tools/dev/up.sh starts it, and the developer exports {MailpitUrlVariable} (for example http://127.0.0.1:8025) before running this test.");

        return url!;
    }

    /// <summary>The message Mailpit holds for <paramref name="address"/>, or <see langword="null"/>.</summary>
    /// <param name="mailpit">The API base URL.</param>
    /// <param name="address">The recipient address the job read from <c>users.email</c>.</param>
    /// <param name="ct">Cancels the read.</param>
    private static async Task<MailpitMessage?> FindDeliveredMessageAsync(
        string mailpit,
        string address,
        CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var page = await client.GetFromJsonAsync<MailpitPage>($"{mailpit.TrimEnd('/')}/api/v1/messages?limit=50", ct);

        return page?.Messages.FirstOrDefault(message =>
            message.To.Any(recipient => string.Equals(recipient.Address, address, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The stored body of one message, or <see langword="null"/> when Mailpit lost it.</summary>
    /// <param name="mailpit">The API base URL.</param>
    /// <param name="id">The Mailpit message id.</param>
    /// <param name="ct">Cancels the read.</param>
    private static async Task<MailpitMessageDetail?> ReadMessageAsync(string mailpit, string id, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        return await client.GetFromJsonAsync<MailpitMessageDetail>($"{mailpit.TrimEnd('/')}/api/v1/message/{id}", ct);
    }

    /// <summary>One page of Mailpit's message list.</summary>
    /// <param name="Messages">The messages, newest first.</param>
    private sealed record MailpitPage(IReadOnlyList<MailpitMessage> Messages);

    /// <summary>One message of the list.</summary>
    /// <param name="Id">The Mailpit id.</param>
    /// <param name="Subject">The received subject.</param>
    /// <param name="To">The recipients Mailpit parsed.</param>
    private sealed record MailpitMessage(string Id, string Subject, IReadOnlyList<MailpitAddress> To);

    /// <summary>One recipient of one message.</summary>
    /// <param name="Address">The address.</param>
    private sealed record MailpitAddress(string Address);

    /// <summary>One stored message, with its rendered bodies.</summary>
    /// <param name="Subject">The received subject.</param>
    /// <param name="Text">The <c>text/plain</c> body.</param>
    private sealed record MailpitMessageDetail(string Subject, string Text);
}
