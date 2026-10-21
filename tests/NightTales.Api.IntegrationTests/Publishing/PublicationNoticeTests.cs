using System.Globalization;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Email.Jobs;
using NightTales.Application.Features.Publishing.Commands.ConsentPublication;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The two e-mails of 06.2.2-T2 (06 README D7, D11, D17, YR8, YR9): the consent receipt every consent queues and
/// the veto notice every autopilot publication queues with its single-use cancel link.
/// </summary>
/// <remarks>
/// <para>
/// Both are produced inside the command's own unit of work — the notice joins the create's transaction, the receipt
/// joins the consent's — so the class drives the two commands through the real Mediator pipeline of the Api host
/// (the seam 02.8.3 will use) and asserts what the pipeline committed.
/// </para>
/// <para>
/// The token is the point of the class: it must not exist when the notice is queued, must be minted by
/// <c>SendEmailJob</c> at send time (09 README §B10 WT17, WT30), must be stored as a hash only, and must travel in the
/// rendered body's fragment. The job is run from the Api host's own scope with only the SMTP transport replaced, so
/// the real token store, renderer and minter chain are the ones asserted.
/// </para>
/// <para>
/// Every assertion is scoped to the ids this test seeded, because the class shares one database (D13).
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class PublicationNoticeTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The flag the publishing commands read.</summary>
    private const string FeatureKey = "youtube_publish";

    /// <summary>The consent receipt template (06 CR-6).</summary>
    private const string ConsentedTemplate = "publication_consented";

    /// <summary>The veto notice template (06 CR-6).</summary>
    private const string VetoNoticeTemplate = "publication_veto_notice";

    /// <summary>The outbox type the create raises; the notice's own row is <c>email_requested.v1</c>.</summary>
    private const string OutboxEventType = "publication_updated.v1";

    /// <summary>09.3.3-T1's kind value the minter issues (06 CR-6).</summary>
    private const string CancelTokenKind = "publication_cancel";

    /// <summary>The default <c>YouTube:ConsentTextVersion</c> the host serves (D11).</summary>
    private const string ConsentTextVersion = "pc.v1";

    /// <summary>YR1's window floor, so the autopilot create below is legal.</summary>
    private const string VetoWindowTwoHours = "{\"veto_window_hours\": 2}";

    private static readonly DateTimeOffset Start = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] Tags = ["đêm", "truyện"];

    /// <summary>The exact producer key set of <c>publication_consented</c> (06 CR-6's allow-list).</summary>
    private static readonly string[] ConsentedParamKeys =
    [
        "approvalLine", "channelTitle", "consentSource", "displayName", "privacy", "privacyNote",
        "publicationUrl", "scheduledAt", "videoTitle",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Consent_QueuesOneReceiptAndAnIdempotentRetryQueuesNothing()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        var smtp = new FakeSmtpEmailSender();
        using var factory = NewFactory(smtp: smtp);

        var first = await ConsentAsync(factory, world);

        first.IsError.ShouldBeFalse(Describe(first));

        var messageId = SingleEmailId(world.UserId, ConsentedTemplate);

        // D16: the receipt renders in the owner's own ui_locale, and 101's allow-list is the only key set the
        // producer may send — the cat's keys are what the renderer has slots for.
        EmailColumn(messageId, "locale").ShouldBe("vi");
        ParamsKeys(messageId).ShouldBe(ConsentedParamKeys, ignoreOrder: true);

        // The receipt carries no capability: nothing token-shaped is queued (WT17).
        Count(
            $"SELECT count(*) FROM email_messages WHERE id = '{messageId}' "
            + "AND params::text ~* '(token|cancelurl|link)'").ShouldBe(0);

        // #12: the retry arm answers the same DTO and writes nothing — the receipt is queued once per consent.
        var retry = await ConsentAsync(factory, world);

        retry.IsError.ShouldBeFalse(Describe(retry));
        retry.Value.Id.ShouldBe(first.Value.Id);
        EmailCount(world.UserId, ConsentedTemplate).ShouldBe(1, "a retried consent queues no second receipt");
        AuditCount(world.UserId, "publishing.consented").ShouldBe(1);

        // vi is rendered for real once: the job's own render of the queued row.
        await RunJobAsync(factory, messageId);

        smtp.Sent.Count.ShouldBe(1);
        (smtp.Sent[0].TextBody ?? string.Empty).ShouldContain("đã được duyệt để đăng");
    }

    [Fact]
    public async Task Consent_QueueFailure_FailsTheWholeCommand()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory(smtp: new FakeSmtpEmailSender(), failQueueing: true);

        var consent = await ConsentAsync(factory, world);

        // 06.2.2-T2 §Failure handling: the receipt is the consent's proof of announcement, so a queueing failure
        // fails the command — no consent_at, no episode transition, no outbox row and no half-written e-mail.
        consent.IsError.ShouldBeTrue("the queue refused the receipt");
        consent.FirstError.Code.ShouldBe(ErrorCodes.EmailSendFailed);

        Scalar($"SELECT coalesce(consent_at::text, '') FROM publications WHERE id = '{world.PublicationId}'")
            .ShouldBeEmpty();
        Scalar($"SELECT status FROM publications WHERE id = '{world.PublicationId}'").ShouldBe("awaiting_consent");
        Scalar($"SELECT status FROM episodes WHERE id = '{world.ConsentEpisodeId}'").ShouldBe("ready");
        Count(
            $"SELECT count(*) FROM outbox_messages WHERE type = '{OutboxEventType}' "
            + $"AND payload::text LIKE '%{world.PublicationId}%'").ShouldBe(0);
        EmailCount(world.UserId, ConsentedTemplate).ShouldBe(0);
    }

    [Fact]
    public async Task VetoNotice_RecordsTheEmailMessageIdAndMintsTheCancelLinkAtSendTime()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        var smtp = new FakeSmtpEmailSender();
        using var factory = NewFactory(smtp: smtp);

        var created = await AutopilotCreateAsync(factory, world);

        created.IsError.ShouldBeFalse(Describe(created));

        var publicationId = created.Value.Id;
        var messageId = SingleEmailId(world.UserId, VetoNoticeTemplate);

        // D7: the one handle the row keeps is the queued message's id, in `platform_payload`, and D8's
        // `veto_notice_sent_at` stays null because 06.2.2-T3 writes it from the delivery status.
        Scalar($"SELECT platform_payload ->> 'vetoNoticeEmailId' FROM publications WHERE id = '{publicationId}'")
            .ShouldBe(messageId.ToString());
        Scalar(
            $"SELECT coalesce(veto_notice_sent_at::text, '') FROM publications WHERE id = '{publicationId}'")
            .ShouldBeEmpty();

        EmailColumn(messageId, "locale").ShouldBe("vi");

        // The notice's deadline and the id the minter needs are producer keys, and the token is not there yet:
        // nothing token-shaped is queued, and no cancel token exists before the job runs (WT17, WT30).
        ParamsKeys(messageId).ShouldContain("publicationId");
        ParamsKeys(messageId).ShouldContain("vetoEndsAt");
        ParamsKeys(messageId).ShouldNotContain("cancelUrl");
        Count($"SELECT count(*) FROM email_messages WHERE id = '{messageId}' AND params IS NULL").ShouldBe(0);
        CancelTokenCount(publicationId).ShouldBe(0, "the queued notice carries no capability");

        // Acceptance #4: a pending audit replaced the submitted `public` and the note names what was submitted.
        Scalar($"SELECT params ->> 'privacy' FROM email_messages WHERE id = '{messageId}'").ShouldBe("private");
        Scalar($"SELECT params ->> 'privacyNote' FROM email_messages WHERE id = '{messageId}'")
            .ShouldContain("public");

        await RunJobAsync(factory, messageId);

        smtp.Sent.Count.ShouldBe(1);

        var body = smtp.Sent[0].TextBody ?? string.Empty;
        var fragment = $"/publications/cancel#p={publicationId:D}&t=";

        body.ShouldContain(fragment);

        var token = body[(body.IndexOf(fragment, StringComparison.Ordinal) + fragment.Length)..]
            .Split(['\r', '\n', ' ', ')', ']', '>'], StringSplitOptions.RemoveEmptyEntries)[0];

        token.Length.ShouldBeGreaterThan(20);

        // YR8/WT17: one single-use row, scoped to the publication, stored as a hash — the plaintext exists only
        // in the body the transport accepted.
        CancelTokenCount(publicationId).ShouldBe(1);
        Scalar($"SELECT kind FROM user_action_tokens WHERE subject_id = '{publicationId}'").ShouldBe(CancelTokenKind);
        Scalar($"SELECT token_hash FROM user_action_tokens WHERE subject_id = '{publicationId}'")
            .ShouldNotBe(token);
        Count(
            $"SELECT count(*) FROM user_action_tokens WHERE subject_id = '{publicationId}' "
            + $"AND token_hash = '{token}'").ShouldBe(0);
        Scalar(
            $"SELECT user_id FROM user_action_tokens WHERE subject_id = '{publicationId}'").ShouldBe(
            world.UserId.ToString());

        // The row is the audit trail of what was sent, not a second copy of it (WT17): params is dropped on send,
        // and the token appears nowhere in any stored payload.
        Count($"SELECT count(*) FROM email_messages WHERE id = '{messageId}' AND params IS NULL").ShouldBe(1);
        Count(
            $"SELECT count(*) FROM email_messages WHERE user_id = '{world.UserId}' "
            + $"AND params::text LIKE '%{token}%'").ShouldBe(0);
        Count(
            $"SELECT count(*) FROM outbox_messages WHERE payload::text LIKE '%{token}%'").ShouldBe(0);
        Count(
            $"SELECT count(*) FROM outbox_messages WHERE payload::text ILIKE '%cancelUrl%' "
            + $"AND payload::text LIKE '%{publicationId}%'").ShouldBe(0);
    }

    [Fact]
    public async Task VetoNotice_ApprovedAuditKeepsTheSubmittedPrivacyAndRendersEnglish()
    {
        var world = await SeedAsync(uiLocale: "en");
        await SetFlagAsync(true);
        var smtp = new FakeSmtpEmailSender();
        using var factory = NewFactory(
            smtp: smtp,
            auditStatus: YouTubeAuditStatuses.Approved);

        var created = await AutopilotCreateAsync(factory, world);

        created.IsError.ShouldBeFalse(Describe(created));

        var messageId = SingleEmailId(world.UserId, VetoNoticeTemplate);

        // Acceptance #4's other arm: an approved audit leaves the submitted privacy alone and says nothing about it.
        Scalar($"SELECT params ->> 'privacy' FROM email_messages WHERE id = '{messageId}'").ShouldBe("public");
        Scalar($"SELECT params ->> 'privacyNote' FROM email_messages WHERE id = '{messageId}'").ShouldBeEmpty();

        await RunJobAsync(factory, messageId);

        // en is rendered for real once (the class's other render is the vi receipt). The English phrase the catalog
        // puts in the subject is the subject's, not the body's: the renderer lifts the template's first line into
        // `Subject`, so both halves are asserted and neither may carry the Vietnamese wording (D16).
        EmailColumn(messageId, "locale").ShouldBe("en");
        smtp.Sent.Count.ShouldBe(1);

        var subject = smtp.Sent[0].Subject ?? string.Empty;

        subject.ShouldContain("Review before it publishes");
        subject.ShouldNotContain("Phủ quyết");

        var english = smtp.Sent[0].TextBody ?? string.Empty;

        english.ShouldContain("will publish to YouTube channel");
        english.ShouldNotContain("Phủ quyết");
    }

    /// <summary>
    /// One owner with a live connection, a series whose autopilot window is YR1's floor, a ready episode no
    /// publication names yet — the episode an autopilot create targets — and, for the consent tests, a second ready
    /// episode carrying a publication already <c>awaiting_consent</c>.
    /// </summary>
    /// <remarks>
    /// The two episodes are the point: <c>ux_publications_active_episode_platform</c> keeps one live publication per
    /// episode (06 README D6/D18, its predicate is <c>status NOT IN ('failed','cancelled')</c>, so
    /// <c>awaiting_consent</c> competes), and a create that targeted the consent publication's episode would be
    /// refused by that index with 409 <c>publication_invalid_state</c> instead of exercising the veto notice.
    /// </remarks>
    /// <param name="uiLocale">The owner's <c>ui_locale</c>, which every notice renders in (D16).</param>
    private async Task<World> SeedAsync(string uiLocale = "vi")
    {
        var owner = new TestCurrentUser();
        var userId = (await new UserBuilder(Database, owner).CreateAsync(ct: Ct)).Id;

        if (!string.Equals(uiLocale, "vi", StringComparison.Ordinal))
        {
            await Database.ExecuteAsync(
                $"UPDATE users SET ui_locale = '{uiLocale}' WHERE id = '{userId}'",
                Ct);
        }

        var actor = new TestCurrentUser { Id = userId };
        var clock = new FakeClock(Start);

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            userId,
            title: "Chuỗi thử nghiệm",
            autopilotEnabled: true,
            autopilotSettings: VetoWindowTwoHours,
            createdAt: Start,
            ct: Ct);

        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập thử nghiệm.",
            status: EpisodeStatus.Ready,
            createdAt: Start,
            ct: Ct);

        var consentEpisode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập đã có bản đăng chờ duyệt.",
            status: EpisodeStatus.Ready,
            createdAt: Start,
            ct: Ct);

        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            ChannelConnectionStatus.Active,
            $"NTS Test Channel {Guid.CreateVersion7():N}",
            ct: Ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            consentEpisode.Id,
            connection.Id,
            status: PublicationStatus.AwaitingConsent,
            title: "Ngôi nhà bỏ hoang",
            createdAt: Start,
            ct: Ct);

        return new World(userId, series.Id, episode.Id, connection.Id, publicationId, consentEpisode.Id);
    }

    /// <summary>Writes the one flag row these commands read.</summary>
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

    /// <summary>
    /// The host. The audit status, the SMTP fake and the failing sender are in the container <em>before</em> the first
    /// <c>Services</c> read, because the flag cache and the options monitor are built when the host starts.
    /// </summary>
    /// <param name="smtp">The transport the send job is asserted through.</param>
    /// <param name="auditStatus">The audit status the create reads (D11).</param>
    /// <param name="failQueueing">Replaces <c>IEmailSender</c> with a sender that refuses every message.</param>
    private ApiFactory NewFactory(
        FakeSmtpEmailSender smtp,
        string auditStatus = YouTubeAuditStatuses.Pending,
        bool failQueueing = false)
    {
        var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithSetting($"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.AuditStatus)}", auditStatus)
            .WithServices(services =>
            {
                // The job class is registered by the Worker's own job scan; the Api host builds this graph for
                // the class's send-time assertions, so it adds the one job those assertions activate.
                services.TryAddScoped<SendEmailJob>();

                services.RemoveAll<NightTales.Application.Abstractions.Email.ISmtpEmailSender>();
                services.AddSingleton<NightTales.Application.Abstractions.Email.ISmtpEmailSender>(smtp);

                if (failQueueing)
                {
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender, RefusingEmailSender>();
                }
            });

        return factory;
    }

    /// <summary>The consent command, sent the way the route sends it (06.2.2-T1, D7).</summary>
    /// <param name="factory">The host.</param>
    /// <param name="world">The seeded owner.</param>
    private async Task<ErrorOr<PublicationDto>> ConsentAsync(ApiFactory factory, World world)
    {
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);

        // 09.1.4-T1: the If-Match the route forwards is the row's current xmin, so the retry below re-reads it.
        var command = new ConsentPublicationCommand(
            world.PublicationId,
            ConsentTextVersion,
            ETags.Publication(Xmin(world.PublicationId)));

        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command, Ct);
    }

    /// <summary>The autopilot create: the seam 02.8.3 drives, through the same Mediator pipeline.</summary>
    /// <param name="factory">The host.</param>
    /// <param name="world">The seeded owner, whose series carries a live authorization.</param>
    private async Task<ErrorOr<PublicationDto>> AutopilotCreateAsync(ApiFactory factory, World world)
    {
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var authorizationId = await SeedAuthorizationAsync(world);

        var command = new CreatePublicationCommand(
            world.EpisodeId,
            world.ConnectionId,
            "Ngôi nhà bỏ hoang",
            "Nội dung mô tả thử nghiệm cho video YouTube.",
            Tags,
            "public",
            Start.AddHours(5),
            authorizationId);

        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command, Ct);
    }

    /// <summary>
    /// Runs the one <c>SendEmailJob</c> of a message in the host's own scope, the way Hangfire activates it. The
    /// outbox is not dispatched: the row is queued, and the send is the span this class asserts (mint, render, SMTP).
    /// </summary>
    /// <param name="factory">The host.</param>
    /// <param name="emailMessageId">The message to send.</param>
    private static async Task RunJobAsync(ApiFactory factory, Guid emailMessageId)
    {
        using var scope = factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<SendEmailJob>();

        await job.RunAsync(emailMessageId, Ct);
    }

    /// <summary>
    /// One live autopilot authorization of the series, written straight into 02's table: the handler only reads it,
    /// and 02.8.3-T2 owns every write on that path.
    /// </summary>
    /// <param name="world">The seeded owner.</param>
    private async Task<Guid> SeedAuthorizationAsync(World world)
    {
        var id = Guid.CreateVersion7();
        var stamp = $"'{Start.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz";

        await Database.ExecuteAsync(
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, "
            + "text_hash, topic, channel_connection_id, cadence, privacy_status, accepted_at, revoked_at, "
            + "created_at, updated_at) "
            + $"VALUES ('{id}', '{world.UserId}', '{world.SeriesId}', 1, 'pc.v1', '{new string('a', 64)}', "
            + $"'ghost', '{world.ConnectionId}', 'daily', 'public', {stamp}, NULL, {stamp}, {stamp})",
            Ct);

        return id;
    }

    /// <summary>The one message id of a template for an owner; more than one is a failure.</summary>
    /// <param name="userId">The recipient.</param>
    /// <param name="template">The catalog template name.</param>
    private Guid SingleEmailId(Guid userId, string template)
    {
        var ids = SqlProbe.QueryStrings(
            Database,
            $"SELECT id::text FROM email_messages WHERE user_id = '{userId}' AND template = '{template}' "
            + "ORDER BY created_at");

        ids.Count.ShouldBe(1, $"one '{template}' message is queued for this owner");

        return Guid.Parse(ids[0]);
    }

    /// <summary>How many messages of a template an owner has.</summary>
    /// <param name="userId">The recipient.</param>
    /// <param name="template">The catalog template name.</param>
    private int EmailCount(Guid userId, string template) =>
        Count($"SELECT count(*) FROM email_messages WHERE user_id = '{userId}' AND template = '{template}'");

    /// <summary>The keys of one message's queued <c>params</c>.</summary>
    /// <param name="emailMessageId">The message.</param>
    private List<string> ParamsKeys(Guid emailMessageId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT jsonb_object_keys(params) FROM email_messages WHERE id = '{emailMessageId}'");

    /// <summary>One text column of one message.</summary>
    /// <param name="emailMessageId">The message.</param>
    /// <param name="column">The column name, spelled by this test only.</param>
    private string EmailColumn(Guid emailMessageId, string column) =>
        Scalar($"SELECT {column} FROM email_messages WHERE id = '{emailMessageId}'");

    /// <summary>Live cancel tokens of one publication (YR8: at most one is ever current).</summary>
    /// <param name="publicationId">The publication the token is scoped to.</param>
    private int CancelTokenCount(Guid publicationId) =>
        Count(
            $"SELECT count(*) FROM user_action_tokens WHERE subject_id = '{publicationId}' "
            + $"AND kind = '{CancelTokenKind}'");

    /// <summary>This command's audit rows for one owner (D17: ids only).</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="action">The audit action.</param>
    private int AuditCount(Guid userId, string action) =>
        Count(
            $"SELECT count(*) FROM audit_logs WHERE action = '{action}' "
            + $"AND coalesce(subject_user_id, actor_user_id) = '{userId}'");

    /// <summary>The <c>xmin</c> of the row, as <c>ETags.Publication</c> spells it.</summary>
    /// <param name="publicationId">The publication.</param>
    private uint Xmin(Guid publicationId) =>
        uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM publications WHERE id = '{publicationId}'"),
            CultureInfo.InvariantCulture);

    /// <summary>One scalar, through the one probe helper every assertion here uses.</summary>
    /// <param name="sql">A single-value query this test wrote.</param>
    private string Scalar(string sql) => SqlProbe.ScalarText(Database, sql);

    /// <summary>One count, through the same helper.</summary>
    /// <param name="sql">A single-count query this test wrote.</param>
    private int Count(string sql) => (int)SqlProbe.Count(Database, sql);

    /// <summary>The code list of a failure, so a broken test says why.</summary>
    /// <param name="result">The result the command answered.</param>
    private static string Describe<T>(ErrorOr<T> result) =>
        result.IsError
            ? string.Join(", ", result.Errors.Select(error => error.Code))
            : "no error";

    /// <summary>
    /// One seeded owner: the episode a create targets, the episode the consent publication names (a second one,
    /// because one episode carries one live publication), its series and the connection it would upload through.
    /// </summary>
    /// <param name="UserId">The owner.</param>
    /// <param name="SeriesId">The series whose autopilot authorization the create checks.</param>
    /// <param name="EpisodeId">The episode the create targets.</param>
    /// <param name="ConnectionId">The active connection.</param>
    /// <param name="PublicationId">The <c>awaiting_consent</c> publication the consent tests drive.</param>
    /// <param name="ConsentEpisodeId">The episode that publication names; the create target is a different one.</param>
    private sealed record World(
        Guid UserId,
        Guid SeriesId,
        Guid EpisodeId,
        Guid ConnectionId,
        Guid PublicationId,
        Guid ConsentEpisodeId);

    /// <summary>
    /// An <c>IEmailSender</c> that refuses every message: the shape of a queueing failure the command must not
    /// commit through (06.2.2-T2 §Failure handling).
    /// </summary>
    private sealed class RefusingEmailSender : IEmailSender
    {
        /// <inheritdoc />
        public Task<ErrorOr<Guid>> QueueAsync(
            NightTales.Application.Abstractions.Email.EmailMessage message,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);

            return Task.FromResult<ErrorOr<Guid>>(
                Error.Failure(
                    ErrorCodes.EmailSendFailed,
                    "the transport refused the message"));
        }
    }
}
