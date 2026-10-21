using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Features.Email;
using NightTales.Application.Features.Email.Jobs;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Email.TokenMinters;
using NightTales.Application.Features.Publishing.Commands.ConsentPublication;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;
using EmailAbstractions = NightTales.Application.Abstractions.Email;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The end-to-end capability probe of 06.2.2-T2's veto notice: one autopilot create queues the notice, the send job
/// mints the single-use cancel link into the rendered body only, and the plaintext token is nowhere else — not in
/// <c>email_messages.params</c>, not in an outbox payload, not in the audit row, not in a log line — before and
/// after the link cancels the publication.
/// </summary>
/// <remarks>
/// Independent QA pass: it drives the same seams the product does (the create command through the Mediator pipeline
/// and <see cref="SendEmailJob"/> through the host's own scope) and replaces only the SMTP transport.
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class PublicationCancelLinkEmailQaTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The anonymous cancel-by-link route of 06.2.2-T2.</summary>
    private const string Route = "/api/v1/publications/cancel-by-link";

    private const string FeatureKey = "youtube_publish";

    private const string VetoNoticeTemplate = EmailTemplateCatalog.PublicationVetoNotice;

    private const string ConsentTextVersion = "pc.v1";

    private const string VetoWindowTwoHours = "{\"veto_window_hours\": 2}";

    /// <summary>Words only this test's rows carry, so a log or an audit row can be proven free of them.</summary>
    private const string TitleCanary = "Tiêu đề canary link QA";

    private const string DescriptionCanary = "Mô tả canary link QA";

    private static readonly DateTimeOffset Start = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);

    private static readonly string[] Tags = ["đêm", "canary"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CancelLink_ExistsOnlyInTheRenderedBody_AndTheLinkCancelsExactlyOnce()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        var smtp = new FakeSmtpEmailSender();
        var sink = new TestLogSink();
        using var factory = NewFactory(smtp, sink);

        var created = await AutopilotCreateAsync(factory, world);

        created.IsError.ShouldBeFalse(string.Join(", ", created.Errors.Select(error => error.Code)));

        var publicationId = created.Value.Id;
        var messageId = SingleEmailId(world.UserId);

        // Acceptance #1/#2: the queued notice carries only the catalog's producer keys, and no row of the token
        // table exists before the send job runs (WT17: the capability is minted at send time).
        var definition = EmailTemplateCatalog.Find(VetoNoticeTemplate).ShouldNotBeNull();
        var queuedKeys = ParamsKeys(messageId);

        queuedKeys.ShouldAllBe(key => definition.ProducerKeys.Contains(key), $"queued keys: {string.Join(',', queuedKeys)}");
        queuedKeys.ShouldNotContain(PublicationCancelTokenMinter.CancelUrlKey);
        TokenRowCount(publicationId).ShouldBe(0, "the queued notice carries no capability");

        await RunJobAsync(factory, messageId);

        smtp.Sent.Count.ShouldBe(1);

        var body = smtp.Sent[0].TextBody ?? string.Empty;
        var html = smtp.Sent[0].HtmlBody ?? string.Empty;
        var fragment = $"/publications/cancel#p={publicationId:D}&t=";

        body.ShouldContain(fragment);

        var token = body[(body.IndexOf(fragment, StringComparison.Ordinal) + fragment.Length)..]
            .Split(['\r', '\n', ' ', ')', ']', '>'], StringSplitOptions.RemoveEmptyEntries)[0];

        token.Length.ShouldBeGreaterThan(20);

        // The html body carries the same link, and the `&` between the fragment members is the escaped one (the
        // renderer, not the template, owns that rule).
        html.ShouldContain(fragment.Replace("&", "&amp;", StringComparison.Ordinal));

        // One row, hashed, scoped to the publication — and the plaintext is in the body only.
        TokenRowCount(publicationId).ShouldBe(1);
        Scalar($"SELECT token_hash FROM user_action_tokens WHERE subject_id = '{publicationId}'")
            .ShouldNotBe(token);
        Count(
            $"SELECT count(*) FROM email_messages WHERE user_id = '{world.UserId}' "
            + $"AND params::text LIKE '%{token}%'").ShouldBe(0);
        Count($"SELECT count(*) FROM email_messages WHERE id = '{messageId}' AND params IS NULL").ShouldBe(1);
        Count($"SELECT count(*) FROM outbox_messages WHERE payload::text LIKE '%{token}%'").ShouldBe(0);
        Count($"SELECT count(*) FROM audit_logs WHERE data::text LIKE '%{token}%'").ShouldBe(0);
        Count($"SELECT count(*) FROM user_action_tokens WHERE token_hash = '{token}'").ShouldBe(0);

        // The cancel itself, anonymously, through the same link.
        using var client = factory.AnonymousClient();

        using (var response = await client.PostAsJsonAsync(
            Route,
            new { publicationId, token },
            Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        }

        Scalar($"SELECT status FROM publications WHERE id = '{publicationId}'").ShouldBe("cancelled");
        Count(
            $"SELECT count(*) FROM user_action_tokens WHERE subject_id = '{publicationId}' "
            + "AND used_at IS NOT NULL").ShouldBe(1);

        // The cancellation's own outbox row and audit row carry ids and statuses only (D13/D17).
        // The create already raised one row (`awaiting_consent`); the cancellation adds exactly one more.
        var payloads = CancelPayloads(publicationId);

        payloads.Count.ShouldBe(1);

        foreach (var payload in payloads)
        {
            payload.ShouldNotContain(token, Case.Sensitive);
            payload.ShouldNotContain(TitleCanary, Case.Sensitive);
        }

        var data = SqlProbe.QueryStrings(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = 'publishing.cancelled' "
            + $"AND entity_id = '{publicationId}'");

        data.Count.ShouldBe(1);
        data[0].ShouldNotContain(token, Case.Sensitive);
        data[0].ShouldNotContain(TitleCanary, Case.Sensitive);
        data[0].ShouldNotContain(DescriptionCanary, Case.Sensitive);
        data[0].ShouldContain("system:link");

        // Logs: no token, no title, no description, and no owner address (D17).
        var log = sink.AllText();

        log.ShouldNotBeNullOrWhiteSpace("the sink must have seen this host's lines, or the search proves nothing");
        log.ShouldNotContain(token, Case.Sensitive);
        log.ShouldNotContain(TitleCanary, Case.Sensitive);
        log.ShouldNotContain(DescriptionCanary, Case.Sensitive);
        log.ShouldNotContain(world.Address, Case.Sensitive);

        // Replay: the token is spent, so the same link answers the generic refusal and writes nothing new.
        using (var replay = await client.PostAsJsonAsync(Route, new { publicationId, token }, Ct))
        {
            replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        CancelPayloads(publicationId).Count.ShouldBe(1, "the replay raises no second publication_updated.v1");
    }

    [Fact]
    public async Task ConsentQueueFailure_FailsTheWholeCommand_AndLogsTheTemplateNameAndAUserHash()
    {
        // Acceptance check 11: a queueing failure is a code-bug surface, and the whole consent fails — no
        // consent_at, no episode transition, no outbox row — while the log names the template and a user-id hash
        // and never the owner's address.
        var world = await SeedAsync();
        await SetFlagAsync(true);
        var sink = new TestLogSink();
        using var factory = NewFactory(new FakeSmtpEmailSender(), sink, refusing: true);

        var consent = await ConsentAsync(factory, world);

        consent.IsError.ShouldBeTrue("the queue refused the receipt");
        consent.FirstError.Code.ShouldBe(ErrorCodes.EmailSendFailed);

        Scalar($"SELECT coalesce(consent_at::text, '') FROM publications WHERE id = '{world.ConsentPublicationId}'")
            .ShouldBeEmpty();
        Scalar($"SELECT status FROM publications WHERE id = '{world.ConsentPublicationId}'")
            .ShouldBe("awaiting_consent");
        Scalar($"SELECT status FROM episodes WHERE id = '{world.ConsentEpisodeId}'").ShouldBe("ready");
        Count(
            $"SELECT count(*) FROM outbox_messages WHERE type = 'publication_updated.v1' "
            + $"AND payload->>'publicationId' = '{world.ConsentPublicationId}'").ShouldBe(0);
        Count(
            $"SELECT count(*) FROM email_messages WHERE user_id = '{world.UserId}' "
            + "AND template = 'publication_consented'").ShouldBe(0);

        var log = sink.AllText();

        log.ShouldContain("publication_consented");
        log.ShouldContain(UserHash(world.UserId));
        log.ShouldNotContain(world.Address, Case.Sensitive);
    }

    /// <summary>One owner whose series carries a live autopilot authorization.</summary>
    private async Task<World> SeedAsync()
    {
        var owner = new TestCurrentUser();
        var userId = (await new UserBuilder(Database, owner).CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = userId };
        var clock = new FakeClock(Start);

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            userId,
            title: "Chuỗi thử nghiệm QA link",
            autopilotEnabled: true,
            autopilotSettings: VetoWindowTwoHours,
            createdAt: Start,
            ct: Ct);

        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập thử nghiệm QA.",
            status: EpisodeStatus.Ready,
            createdAt: Start,
            ct: Ct);

        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            ChannelConnectionStatus.Active,
            $"NTS QA Link Channel {Guid.CreateVersion7():N}",
            ct: Ct);

        // The consent tests need a live row of their own: one episode carries at most one active publication.
        var consentEpisode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập chờ duyệt QA.",
            status: EpisodeStatus.Ready,
            createdAt: Start,
            ct: Ct);

        var consentPublicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            consentEpisode.Id,
            connection.Id,
            status: PublicationStatus.AwaitingConsent,
            title: "Tập chờ duyệt QA",
            createdAt: Start,
            ct: Ct);

        var address = Scalar($"SELECT email FROM users WHERE id = '{userId}'");

        return new World(
            userId,
            series.Id,
            episode.Id,
            connection.Id,
            address,
            consentEpisode.Id,
            consentPublicationId);
    }

    private async Task SetFlagAsync(bool enabled)
    {
        await Database.ExecuteAsync($"DELETE FROM feature_flags WHERE key = '{FeatureKey}'", Ct);

        if (enabled)
        {
            await Database.ExecuteAsync(
                "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
                + $"VALUES ('{FeatureKey}', true, '{{}}', now(), now())",
                Ct);
        }
    }

    private ApiFactory NewFactory(FakeSmtpEmailSender smtp, TestLogSink sink, bool refusing = false) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithServices(services =>
            {
                services.TryAddScoped<SendEmailJob>();

                services.RemoveAll<EmailAbstractions.ISmtpEmailSender>();
                services.AddSingleton<EmailAbstractions.ISmtpEmailSender>(smtp);

                if (refusing)
                {
                    services.RemoveAll<EmailAbstractions.IEmailSender>();
                    services.AddSingleton<EmailAbstractions.IEmailSender, RefusingEmailSender>();
                }

                services.AddSingleton<Serilog.Core.ILogEventSink>(sink);
            });

    private async Task<ErrorOr<PublicationDto>> AutopilotCreateAsync(ApiFactory factory, World world)
    {
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var authorizationId = await SeedAuthorizationAsync(world);

        var command = new CreatePublicationCommand(
            world.EpisodeId,
            world.ConnectionId,
            TitleCanary,
            DescriptionCanary,
            Tags,
            "public",
            Start.AddHours(5),
            authorizationId);

        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command, Ct);
    }

    private async Task<Guid> SeedAuthorizationAsync(World world)
    {
        var id = Guid.CreateVersion7();
        var stamp = $"'{Start.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz";

        await Database.ExecuteAsync(
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, "
            + "text_hash, topic, channel_connection_id, cadence, privacy_status, accepted_at, revoked_at, "
            + "created_at, updated_at) "
            + $"VALUES ('{id}', '{world.UserId}', '{world.SeriesId}', 1, '{ConsentTextVersion}', '{new string('b', 64)}', "
            + $"'ghost', '{world.ConnectionId}', 'daily', 'public', {stamp}, NULL, {stamp}, {stamp})",
            Ct);

        return id;
    }

    private static async Task RunJobAsync(ApiFactory factory, Guid emailMessageId)
    {
        using var scope = factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<SendEmailJob>();

        await job.RunAsync(emailMessageId, Ct);
    }

    private Guid SingleEmailId(Guid userId)
    {
        var ids = SqlProbe.QueryStrings(
            Database,
            $"SELECT id::text FROM email_messages WHERE user_id = '{userId}' AND template = '{VetoNoticeTemplate}'");

        ids.Count.ShouldBe(1, "one veto notice is queued for this owner");

        return Guid.Parse(ids[0], CultureInfo.InvariantCulture);
    }

    private List<string> ParamsKeys(Guid emailMessageId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT jsonb_object_keys(params) FROM email_messages WHERE id = '{emailMessageId}'");

    /// <summary>The <c>publication_updated.v1</c> rows of this cancellation (the create raised its own).</summary>
    /// <param name="publicationId">The publication the rows are about.</param>
    private List<string> CancelPayloads(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            "SELECT payload::text FROM outbox_messages WHERE type = 'publication_updated.v1' "
            + $"AND payload->>'publicationId' = '{publicationId}' AND payload->>'status' = 'cancelled'");

    private int TokenRowCount(Guid publicationId) =>
        Count($"SELECT count(*) FROM user_action_tokens WHERE subject_id = '{publicationId}'");

    private string Scalar(string sql) => SqlProbe.ScalarText(Database, sql);

    private int Count(string sql) => (int)SqlProbe.Count(Database, sql);

    [Fact]
    public async Task AutopilotCreate_QueueFailure_LeavesNoPublicationAndNoNotice()
    {
        // 06.2.2-T2 §Failure handling, first bullet: a queueing failure on the veto-notice path fails the whole
        // create, because the notice is queued inside the same unit of work — nothing half-created, no veto window
        // stored, no outbox row.
        var world = await SeedAsync();
        await SetFlagAsync(true);
        var sink = new TestLogSink();
        using var factory = NewFactory(new FakeSmtpEmailSender(), sink, refusing: true);

        var created = await AutopilotCreateAsync(factory, world);

        created.IsError.ShouldBeTrue("the queue refused the veto notice");
        created.FirstError.Code.ShouldBe(ErrorCodes.EmailSendFailed);

        Count($"SELECT count(*) FROM publications WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0, "the row must not survive a refused notice");
        Count(
            $"SELECT count(*) FROM email_messages WHERE user_id = '{world.UserId}' "
            + "AND template = 'publication_veto_notice'").ShouldBe(0);
        Count(
            $"SELECT count(*) FROM outbox_messages WHERE type = 'publication_updated.v1' "
            + $"AND payload::text LIKE '%{world.EpisodeId}%'").ShouldBe(0);

        var log = sink.AllText();

        log.ShouldContain("publication_veto_notice");
        log.ShouldNotContain(world.Address, Case.Sensitive);
    }

    [Fact]
    public async Task ConsentReceipt_OnAPreAuditRow_ExplainsThePrivacyOverride()
    {
        // Acceptance check 4, third arm: "the same rule holds for publication_consented on a pre-audit-locked
        // row" — the receipt reports the row's effective `private` and names the submitted value in its note,
        // exactly as the veto notice does (both come from the one `PrivacyNote` builder). The pre-audit lock
        // itself is produced by the real writer: an autopilot create submitted as `public` while this host's
        // `YouTube:AuditStatus` is the default `pending` (YouTubeOptions.cs:112).
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory(new FakeSmtpEmailSender(), new TestLogSink());

        var created = await AutopilotCreateAsync(factory, world);

        created.IsError.ShouldBeFalse(string.Join(", ", created.Errors.Select(error => error.Code)));

        var publicationId = created.Value.Id;

        Scalar($"SELECT privacy FROM publications WHERE id = '{publicationId}'")
            .ShouldBe("private", "a pre-audit create overrides the submitted public privacy");
        Scalar($"SELECT platform_payload ->> 'preAudit' FROM publications WHERE id = '{publicationId}'")
            .ShouldBe("true");
        Scalar($"SELECT platform_payload ->> 'requestedPrivacy' FROM publications WHERE id = '{publicationId}'")
            .ShouldBe("public");

        var consent = await ConsentAsync(factory, world, publicationId);

        consent.IsError.ShouldBeFalse(string.Join(", ", consent.Errors.Select(error => error.Code)));

        var messageId = Guid.Parse(
            Scalar(
                $"SELECT id::text FROM email_messages WHERE user_id = '{world.UserId}' "
                + "AND template = 'publication_consented'"),
            CultureInfo.InvariantCulture);

        Scalar($"SELECT params ->> 'privacy' FROM email_messages WHERE id = '{messageId}'")
            .ShouldBe("private");
        Scalar($"SELECT params ->> 'privacyNote' FROM email_messages WHERE id = '{messageId}'")
            .ShouldContain("public");

        var keys = ParamsKeys(messageId);

        keys.ShouldNotContain("cancelUrl");
        keys.ShouldNotContain("publicationId");
        Count(
            $"SELECT count(*) FROM email_messages WHERE id = '{messageId}' "
            + "AND params::text ~* '(token|cancelurl)'").ShouldBe(0);
    }

    /// <summary>The consent command, sent the way the route sends it.</summary>
    /// <param name="factory">The host.</param>
    /// <param name="world">The seeded owner.</param>
    /// <param name="publicationId">The row to consent; the seeded consent row by default.</param>
    private async Task<ErrorOr<PublicationDto>> ConsentAsync(
        ApiFactory factory,
        World world,
        Guid? publicationId = null)
    {
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var id = publicationId ?? world.ConsentPublicationId;

        var command = new ConsentPublicationCommand(
            id,
            ConsentTextVersion,
            ETags.Publication(Xmin(id)));

        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command, Ct);
    }

    /// <summary>The row's <c>xmin</c>, as <c>ETags.Publication</c> spells it.</summary>
    /// <param name="publicationId">The publication.</param>
    private uint Xmin(Guid publicationId) =>
        uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM publications WHERE id = '{publicationId}'"),
            CultureInfo.InvariantCulture);

    /// <summary>The correlation hash <c>PublicationNotices</c> logs beside a failed enqueue (SHA-256, 16 hex).</summary>
    /// <param name="userId">The owner.</param>
    private string UserHash(Guid userId) =>
        Scalar($"SELECT substring(encode(sha256((id::text)::bytea), 'hex'), 1, 16) FROM users WHERE id = '{userId}'");

    private sealed record World(
        Guid UserId,
        Guid SeriesId,
        Guid EpisodeId,
        Guid ConnectionId,
        string Address,
        Guid ConsentEpisodeId,
        Guid ConsentPublicationId);

    /// <summary>An <c>IEmailSender</c> that refuses every message.</summary>
    private sealed class RefusingEmailSender : EmailAbstractions.IEmailSender
    {
        /// <inheritdoc />
        public Task<ErrorOr<Guid>> QueueAsync(
            EmailAbstractions.EmailMessage message,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);

            return Task.FromResult<ErrorOr<Guid>>(
                Error.Failure(ErrorCodes.EmailSendFailed, "the transport refused the message"));
        }
    }
}
