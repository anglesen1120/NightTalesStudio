using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Autopilot.Authorization;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The settings half of the autopilot wire contract (02.8.3-T2 task §Tests rows <c>PutAutopilot_*</c>, §Acceptance
/// "changing <c>daily_cap</c> while enabled", "changing only <c>tone</c>", "stale <c>If-Match</c>", "series archived
/// while enabled"): the order of the refusals of <c>PUT /series/{id}/autopilot</c>, the covered-field revocation
/// matrix, the body limits and the archive path of task §Failure handling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case is asserted through the real HTTP surface and then through the rows it wrote</b>, because the two
/// halves of an answer are different facts: the problem's <c>code</c>/<c>errors</c> is the client contract, and
/// <c>series.autopilot_settings</c>, <c>series_autopilot_authorizations</c> and <c>audit_logs</c> are what the next
/// request reads. A status-only assertion would let a handler that refuses and writes anyway pass.
/// </para>
/// <para>
/// <b>The settings document.</b> The document a test PUTs is <see cref="AutopilotSettingsValidator.Serialize"/>'s
/// canonical text — the same text the handler stores — so a case's moved field is the only difference between the
/// seeded snapshot and the request. The seeded authorization's covered snapshot comes from
/// <see cref="AutopilotAuthorizationCoverage.SnapshotJson"/> with <c>pre_audit_pending: true</c>, which is the
/// host's own answer while <c>YouTube:AuditStatus</c> keeps its default <c>pending</c> (06 README D11).
/// </para>
/// <para>
/// <b>Both stored columns are compared as documents.</b> <c>autopilot_settings</c> is <c>jsonb</c>, and PostgreSQL
/// does not preserve a document's key order, so the comparisons below use <c>jsonb</c> equality rather than the
/// canonical text.
/// </para>
/// <para>
/// <b>One recorded gap.</b> <c>revoked_reason</c> is mapped but has no migration in this worktree yet (S77
/// contract D-1: the orchestrator generates it), so every assertion that reads the column is red until it lands.
/// They are written as the task file states them.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
public sealed class AutopilotSettingsTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The series route prefix of 09 §5.</summary>
    private const string SeriesRoute = "/api/v1/series";

    /// <summary>06 README D9: the flag every autopilot route carries.</summary>
    private const string FeatureKey = "youtube_publish";

    /// <summary>The legal-acceptance route the enable ladder's gate (0) reads (13.2.1-T1).</summary>
    private const string LegalAcceptRoute = "/api/v1/me/legal/accept";

    /// <summary>The filler that pushes a body past the route's 16 KiB cap.</summary>
    private const int PaddingLength = 17 * 1024;

    /// <summary>The instant every test's clock is pinned to: a Thursday, with the next Mon/Thu slot ahead of it.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The cadence every seeded series starts from: Monday and Thursday at 19:00, Ho Chi Minh time.</summary>
    private static readonly DayOfWeek[] Days = [DayOfWeek.Monday, DayOfWeek.Thursday];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The read's contract (task §Acceptance 1): the rendered text carries every covered value, its digest is the
    /// lowercase SHA-256 the accept route compares, and the series-autopilot tag is in the <c>ETag</c> header.
    /// </summary>
    [Fact]
    public async Task GetAutopilot_RendersTextWithEveryCoveredValueAndItsDigest()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: true);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        using var response = await client.SendAsync(
            Request(HttpMethod.Get.Method, $"{Url(world.SeriesId)}?locale=en"),
            Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var rendered = root.GetProperty("authorizationText");

        rendered.GetProperty("textVersion").GetString().ShouldBe(AuthorizationTextRenderer.CurrentTextVersion);
        rendered.GetProperty("locale").GetString().ShouldBe("en");

        var authorizationText = rendered.GetProperty("text").GetString()!;
        authorizationText.ShouldContain("19:00", Case.Sensitive);
        authorizationText.ShouldContain("Asia/Ho_Chi_Minh", Case.Sensitive);
        authorizationText.ShouldContain(ChannelConnectionBuilder.DefaultDisplayName, Case.Sensitive);
        authorizationText.ShouldContain("ghost stories", Case.Sensitive);
        authorizationText.ShouldContain(
            "private until the audit",
            Case.Insensitive,
            "YouTube:AuditStatus defaults to pending, so the pre-audit note is part of this text");
        authorizationText.ShouldNotContain("{{", Case.Sensitive, "every placeholder is substituted");

        var textHash = rendered.GetProperty("textHash").GetString()!;
        textHash.Length.ShouldBe(64);
        textHash.ShouldBe(textHash.ToLowerInvariant(), "the digest is lowercase hex (plan §3.7)");
        textHash.All(char.IsAsciiHexDigitLower).ShouldBeTrue("the digest is lowercase hex (plan §3.7)");

        root.GetProperty("requiredAuthorization").GetBoolean().ShouldBeFalse(
            "the seeded snapshot covers the stored settings, so no re-acceptance is owed");

        response.Headers.ETag.ShouldNotBeNull("the read publishes the tag the write demands");
        response.Headers.ETag!.ToString().ShouldStartWith("\"sa-");
    }

    /// <summary>
    /// Task §Acceptance 2, the accepted half: with a current acceptance, the legal gate satisfied and a fresh
    /// re-authentication, the enable answers 200 with the new tag in the header <em>and</em> the body, switches the
    /// flag on and records <c>autopilot.enabled</c> (plan §4).
    /// </summary>
    [Fact]
    public async Task PutAutopilot_Enable_WritesFlagAuditAndTheNewTagInHeadAndBody()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var tag = await TagAsync(client, world.SeriesId);

        using var response = await PutAsync(client, world.SeriesId, enabled: true, DefaultSettings(), tag);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        using var document = JsonDocument.Parse(text);
        var bodyTag = document.RootElement.GetProperty("etag").GetString();

        response.Headers.ETag.ShouldNotBeNull("the mutating route republishes the tag (09 README D15)");
        var headerTag = response.Headers.ETag!.ToString();
        bodyTag.ShouldBe(headerTag, "both shapes carry one value, so the UI need not read the header");
        headerTag.ShouldNotBe(tag, "the write bumps series.xmin, so the tag the client held is spent");

        Enabled(world.SeriesId).ShouldBeTrue();
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'autopilot.enabled' AND entity_id = '{world.SeriesId}'")
            .ShouldBe(1, "an enable writes one row, and only the handler writes it");

        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(string_agg(coalesce(data ->> 'authorizationId', '') || '|' "
                + "|| coalesce(data ->> 'authorizationVersion', ''), '~'), '') FROM audit_logs "
                + $"WHERE action = 'autopilot.enabled' AND entity_id = '{world.SeriesId}'")
            .ShouldBe(
                $"{world.AuthorizationId}|1",
                "the row names the authorization revision the enable rests on (plan §4: seriesId, settingsHash, "
                + "authorizationId, authorizationVersion)");

        // The document is named by its digest, never copied into the log (contract D-8): the hash of the canonical
        // text the handler stored is the proof of which document the owner consented to.
        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(AutopilotSettingsValidator.Serialize(DefaultSettings()))));

        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(data ->> 'settingsHash', '') || '|' || (data ? 'settings')::text FROM audit_logs "
                + $"WHERE action = 'autopilot.enabled' AND entity_id = '{world.SeriesId}'")
            .ShouldBe(
                $"{expectedHash}|false",
                "settingsHash is the lowercase sha256 of the stored document, and the document itself is absent");
    }

    /// <summary>
    /// Gate (0) of plan §3.7: an owner who has not accepted the current documents cannot consent to uploads. The
    /// client carries no session either, so a 409 rather than a 401 proves legal runs before re-auth.
    /// </summary>
    [Fact]
    public async Task PutAutopilot_Enable_WithoutLegalAcceptance_Returns409WithDocumentsAndWritesNothing()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        var tag = await TagAsync(client, world.SeriesId);
        using var response = await PutAsync(client, world.SeriesId, enabled: true, DefaultSettings(), tag);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ProblemAsync(response);
        problem.Code.ShouldBe("legal_acceptance_required");
        problem.Errors.ShouldNotBeNull("13.2.1-T1 names the documents the owner has to accept");
        problem.Errors!.ShouldContainKey("documents");

        Enabled(world.SeriesId).ShouldBeFalse("a refused enable writes nothing");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'autopilot.enabled' AND entity_id = '{world.SeriesId}'")
            .ShouldBe(0);
    }

    /// <summary>
    /// Gate (1), the exact task-file row: enabling authorizes future uploads without a per-video consent, so the
    /// caller must have re-authenticated within five minutes (09 README BD4). Here the client has no family at all.
    /// </summary>
    [Fact]
    public async Task PutAutopilot_NoReauth_Returns401()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);
        await AcceptLegalAsync(factory, client);

        var tag = await TagAsync(client, world.SeriesId);
        using var response = await PutAsync(client, world.SeriesId, enabled: true, DefaultSettings(), tag);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemAsync(response)).Code.ShouldBe("reauth_required");
        Enabled(world.SeriesId).ShouldBeFalse();
    }

    /// <summary>
    /// The exact task-file row: legal acceptance and a fresh re-auth in place, but no accepted authorization — the
    /// enable answers 409 <c>autopilot_authorization_required</c>.
    /// </summary>
    [Fact]
    public async Task PutAutopilot_NoAuthorization_Returns409()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: false, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var tag = await TagAsync(client, world.SeriesId);
        using var response = await PutAsync(client, world.SeriesId, enabled: true, DefaultSettings(), tag);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(response)).Code.ShouldBe("autopilot_authorization_required");
        Enabled(world.SeriesId).ShouldBeFalse();
    }

    /// <summary>
    /// Gate (3) is a comparison, not a pointer check: a live authorization whose covered snapshot no longer matches
    /// the settings being enabled refuses the enable until the owner accepts the current text.
    /// </summary>
    [Fact]
    public async Task PutAutopilot_StaleAuthorizationSnapshot_Returns409()
    {
        var clock = new FakeClock(Start);
        var accepted = DefaultSettings();
        var stored = accepted with { DailyCap = 2 };
        var world = await SeedAsync(
            clock,
            authorized: true,
            enabled: false,
            settings: stored,
            snapshotSettings: stored);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var tag = await TagAsync(client, world.SeriesId);

        // The series carries daily_cap = 2 and the accepted text named 2; this request asks for 1.
        using var response = await PutAsync(client, world.SeriesId, enabled: true, accepted, tag);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(response)).Code.ShouldBe("autopilot_authorization_required");
        Enabled(world.SeriesId).ShouldBeFalse();
    }

    /// <summary>
    /// The exact task-file row, reached through the enable ladder: a topic that moved since the acceptance is the
    /// comparator's <c>topic_changed</c> case, and the ladder answers it with the same 409 as any other uncovered
    /// field. (The lock on <c>PATCH /series</c> is 09.4.1-T1's and keeps its own code — README D-2.)
    /// </summary>
    [Fact]
    public async Task PutAutopilot_TopicChangeWhileEnabled_Returns409()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(
            clock,
            authorized: true,
            enabled: false,
            topic: Topic.Spiritual,
            authorizedTopic: Topic.Ghost);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var tag = await TagAsync(client, world.SeriesId);
        using var response = await PutAsync(client, world.SeriesId, enabled: true, DefaultSettings(), tag);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(response)).Code.ShouldBe("autopilot_authorization_required");
        Enabled(world.SeriesId).ShouldBeFalse();
    }

    /// <summary>
    /// Gate (4): the default channel must be a live connection. A revoked row is not one, so the enable answers 409
    /// <c>channel_reauth_required</c> — and the same document saves with <c>enabled = false</c>, which is the task
    /// file's "no channel connected" row.
    /// </summary>
    /// <remarks>
    /// The series is off, so the second request is a document edit and not a switch-off: it applies, and because
    /// <c>daily_cap</c> is a covered field the consent it no longer matches is revoked with the comparator's reason.
    /// The switch-off row and the <c>disabled</c> reason belong to a real <c>true → false</c> move only (plan §3.7).
    /// </remarks>
    [Fact]
    public async Task PutAutopilot_ChannelNotActive_Returns409()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: false, channelStatus: ChannelConnectionStatus.Revoked);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var tag = await TagAsync(client, world.SeriesId);
        using var response = await PutAsync(client, world.SeriesId, enabled: true, DefaultSettings(), tag);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(response)).Code.ShouldBe("channel_reauth_required");
        Enabled(world.SeriesId).ShouldBeFalse();

        // A refused enable rolls its transaction back, so the tag the client read still stands.
        var moved = DefaultSettings() with { DailyCap = 3 };
        using var saved = await PutAsync(client, world.SeriesId, enabled: false, moved, tag);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        SettingsColumn(world.SeriesId, "daily_cap").ShouldBe("3", "the document is stored even with the switch off");
        StoredSettingsAre(world.SeriesId, moved).ShouldBeTrue("the published document is the request's, in full");

        // Off already, so nothing was switched off: no `autopilot.disabled` row. `daily_cap` is covered by the
        // accepted text, so the consent is revoked through the comparator with its own reason.
        SqlProbe.ScalarText(Database, AuthorizationColumn(world, "coalesce(revoked_reason, '')"))
            .ShouldBe("text_changed", "a covered edit spends the acceptance whichever way the switch points");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'autopilot.disabled' AND entity_id = '{world.SeriesId}'")
            .ShouldBe(0, "the flag was already false, so no switch-off happened");
    }

    /// <summary>
    /// Step (2) of the ladder: the document is validated before anything else is looked at, and every failure is
    /// keyed by its JSON path under the one code <c>autopilot_settings_invalid</c> (plan §3.1).
    /// </summary>
    [Fact]
    public async Task PutAutopilot_InvalidSettings_Returns400WithErrorsKeyedByJsonPath()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var tag = await TagAsync(client, world.SeriesId);
        var broken = DefaultSettings() with { DailyCap = 99 };
        broken = broken with { Cadence = broken.Cadence with { TimeZoneId = "Not/AZone" } };

        using var response = await PutAsync(client, world.SeriesId, enabled: true, broken, tag);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ProblemAsync(response);
        problem.Code.ShouldBe("autopilot_settings_invalid");
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("daily_cap", "a range failure is keyed by the member the client wrote");
        problem.Errors.ShouldContainKey(
            "cadence.time_zone",
            "a nested failure is keyed by its dotted path, not by `cadence`");
        problem.Errors.Values.ShouldAllBe(messages => messages.Length > 0, "every group names its rule");

        Enabled(world.SeriesId).ShouldBeFalse();
        StoredSettingsAre(world.SeriesId, DefaultSettings())
            .ShouldBeTrue("a 400 leaves the stored document exactly as it was");
    }

    /// <summary>
    /// Mass assignment: the document is a closed vocabulary, so a member the server never accepts is a 400 keyed by
    /// its name instead of a silently stored extra setting.
    /// </summary>
    [Fact]
    public async Task PutAutopilot_UnknownSettingsMember_Returns400AndKeepsTheStoredDocument()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var tag = await TagAsync(client, world.SeriesId);
        var canonical = AutopilotSettingsValidator.Serialize(DefaultSettings());
        var tampered = canonical[..^1] + ",\"is_admin\":true}";

        using var response = await PutAsync(client, world.SeriesId, enabled: true, tampered, tag);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ProblemAsync(response);
        problem.Code.ShouldBe("autopilot_settings_invalid");
        problem.Errors!.ShouldContainKey("is_admin", "the unknown member is named, never ignored");

        StoredSettingsAre(world.SeriesId, DefaultSettings())
            .ShouldBeTrue("the probe member never reaches series.autopilot_settings");
        Enabled(world.SeriesId).ShouldBeFalse();
    }

    /// <summary>
    /// Task §Failure handling row "settings invalid <b>and</b> the authorization would change → 400 first": with the
    /// legal acceptance and the re-authentication already satisfied and <b>no</b> authorization on the series, an
    /// invalid document answers 400 <c>autopilot_settings_invalid</c>. What that proves is the one order this fixture
    /// can isolate: the settings rung runs before the authorization rung, because a 409
    /// <c>autopilot_authorization_required</c> would mean the current-authorization check had been asked first.
    /// </summary>
    /// <remarks>
    /// It proves nothing about the ladder's first two rungs, and does not claim to: the legal gate and the re-auth gate
    /// are both satisfied here, so this case cannot order them against the validator. The implemented order stays the
    /// task file's (line 86): legal → re-auth → settings → authorization → channel, and the failure-table row is the
    /// client-visible statement of what an invalid document wins against — the consent-bearing checks that come after
    /// it, never the two gates that come before.
    /// </remarks>
    [Fact]
    public async Task PutAutopilot_InvalidSettingsAndNoAuthorization_Answers400First()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: false, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var tag = await TagAsync(client, world.SeriesId);
        using var response = await PutAsync(
            client,
            world.SeriesId,
            enabled: true,
            DefaultSettings() with { VetoWindowHours = 1 },
            tag);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(response)).Code.ShouldBe("autopilot_settings_invalid");
    }

    /// <summary>
    /// <c>If-Match</c> is the lost-update guard of the settings write (09 README D9, 02 README D9): an absent header
    /// answers 428 <c>if_match_required</c>; a tag the client did not read, a weak tag and the star all answer 412
    /// <c>series_conflict</c>.
    /// </summary>
    /// <param name="ifMatch">The header value, or null for the absent case.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("\"sa-0\"")]
    [InlineData("*")]
    [InlineData("W/\"sa-1\"")]
    public async Task PutAutopilot_IfMatchIsRequiredAndStrict(string? ifMatch)
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: true);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        using var response = await PutAsync(
            client,
            world.SeriesId,
            enabled: true,
            DefaultSettings() with { Tone = "khac" },
            ifMatch);

        response.StatusCode.ShouldBe(
            ifMatch is null ? HttpStatusCode.PreconditionRequired : HttpStatusCode.PreconditionFailed);
        (await ProblemAsync(response)).Code.ShouldBe(ifMatch is null ? "if_match_required" : "series_conflict");
        StoredSettingsAre(world.SeriesId, DefaultSettings())
            .ShouldBeTrue("a refused tag leaves the document untouched");
    }

    /// <summary>
    /// The 16 KiB cap of S71-48 is declared on the route as well as in the bounded read, so a body that grows past it
    /// answers 413 <c>payload_too_large</c> without reaching the validator.
    /// </summary>
    [Fact]
    public async Task PutAutopilot_OversizedBody_Returns413()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: true);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        var tag = await TagAsync(client, world.SeriesId);
        var oversized = "{\"enabled\":true,\"padding\":\"" + new string('a', PaddingLength) + "\",\"settings\":"
            + AutopilotSettingsValidator.Serialize(DefaultSettings()) + "}";

        using var response = await client.SendAsync(
            Request(HttpMethod.Put.Method, Url(world.SeriesId), oversized, tag),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await ProblemAsync(response)).Code.ShouldBe("payload_too_large");
    }

    /// <summary>
    /// Task §Acceptance 5, one case per covered field: changing it while autopilot is on revokes the authorization
    /// with <c>text_changed</c>, clears the pointer, and makes the read ask for a new acceptance. The comparator reads
    /// the stored snapshot, never a diff of the rendered text, which is why every field of the cadence answers with
    /// the same reason.
    /// </summary>
    /// <param name="field">The covered field the request moves.</param>
    [Theory]
    [InlineData("cadence.publish_time")]
    [InlineData("cadence.days_of_week")]
    [InlineData("cadence.time_zone")]
    [InlineData("daily_cap")]
    [InlineData("privacy")]
    [InlineData("veto_window_hours")]
    [InlineData("pre_audit_mode")]
    public async Task PutAutopilot_CoveredFieldDiff_RevokesWithTextChanged(string field)
    {
        var clock = new FakeClock(Start);
        var accepted = DefaultSettings();
        var world = await SeedAsync(clock, authorized: true, enabled: true);
        using var factory = world.Factory;

        // A settings-only change of an enabled series is not a consent-bearing action: no legal gate, no re-auth.
        using var client = Owner(world, reauthenticated: false);

        var tag = await TagAsync(client, world.SeriesId);
        var moved = Move(accepted, field);

        using var response = await PutAsync(client, world.SeriesId, enabled: true, moved, tag);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        SqlProbe.ScalarText(Database, AuthorizationColumn(world, "(revoked_at IS NOT NULL)::text"))
            .ShouldBe("true", $"a change to {field} is covered by the accepted text");
        SqlProbe.ScalarText(Database, AuthorizationColumn(world, "coalesce(revoked_reason, '')"))
            .ShouldBe("text_changed");
        Pointer(world.SeriesId).ShouldBeNull("a revoked revision is no longer the series' current one");

        // The document itself is applied: the request is an edit of the settings, and only the consent is spent.
        StoredSettingsAre(world.SeriesId, moved)
            .ShouldBeTrue("the settings are written even though the covered change revoked");

        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM audit_logs WHERE action = 'autopilot.authorization_revoked' "
                + $"AND entity_id = '{world.SeriesId}' AND data ->> 'reason' = 'text_changed'")
            .ShouldBe(1, "the revocation is audited once, with the wire reason");

        using var read = await client.SendAsync(Request(HttpMethod.Get.Method, Url(world.SeriesId)), Ct);
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("requiredAuthorization").GetBoolean().ShouldBeTrue(
            "the page has to ask for the new text (task §Acceptance 5)");
        document.RootElement.GetProperty("authorization").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "a revoked revision is never reported as current");
    }

    /// <summary>
    /// Task §Acceptance 6 and plan §3.7's [DECISION]: a field the text never names keeps the authorization, because
    /// the comparator is an explicit member-by-member check.
    /// </summary>
    /// <param name="field">The non-covered field the request moves.</param>
    [Theory]
    [InlineData("tone")]
    [InlineData("banned_themes")]
    [InlineData("gate_thresholds_ref")]
    [InlineData("render_lead_minutes")]
    [InlineData("story_lead_minutes")]
    [InlineData("buffer_days")]
    [InlineData("pre_audit_pause_after")]
    public async Task PutAutopilot_NonCoveredFieldDiff_DoesNotRevoke(string field)
    {
        var clock = new FakeClock(Start);
        var accepted = DefaultSettings();
        var world = await SeedAsync(clock, authorized: true, enabled: true);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        var tag = await TagAsync(client, world.SeriesId);
        var moved = Move(accepted, field);

        using var response = await PutAsync(client, world.SeriesId, enabled: true, moved, tag);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        SqlProbe.ScalarText(Database, AuthorizationColumn(world, "(revoked_at IS NOT NULL)::text"))
            .ShouldBe("false", $"{field} is not part of the accepted text");
        Pointer(world.SeriesId).ShouldBe(
            world.AuthorizationId,
            "the same revision stays current, so no re-acceptance is owed");

        StoredSettingsAre(world.SeriesId, moved)
            .ShouldBeTrue("the change is still applied, which is what the row asks for");

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'autopilot.authorization_revoked' AND entity_id = '{world.SeriesId}'")
            .ShouldBe(0);

        using var read = await client.SendAsync(Request(HttpMethod.Get.Method, Url(world.SeriesId)), Ct);
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("requiredAuthorization").GetBoolean().ShouldBeFalse();
    }

    /// <summary>
    /// The audit-status half of the snapshot (S77 D-7): an acceptance stored while the YouTube audit was pending
    /// records that fact, so a later render without the note is a covered change (<c>text_changed</c>) instead of a
    /// silent difference between the stored text and this build's render (task §Failure handling [DECISION]).
    /// </summary>
    [Fact]
    public async Task PutAutopilot_PreAuditFlagFlip_RevokesWithTextChanged()
    {
        var clock = new FakeClock(Start);
        var accepted = DefaultSettings();
        var world = await SeedAsync(
            clock,
            authorized: true,
            enabled: true,
            snapshotPreAuditPending: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        var tag = await TagAsync(client, world.SeriesId);

        // The host's policy still answers pending, while the stored snapshot was taken once the audit was approved.
        using var response = await PutAsync(client, world.SeriesId, enabled: true, accepted, tag);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        SqlProbe.ScalarText(Database, AuthorizationColumn(world, "coalesce(revoked_reason, '')"))
            .ShouldBe("text_changed", "the pre-audit note is part of the accepted text (D-7)");
        Pointer(world.SeriesId).ShouldBeNull();
    }

    /// <summary>
    /// <c>enabled = false</c> revokes with <c>disabled</c>, needs no re-authentication (it reduces risk), and writes
    /// both rows plan §4 names: the revocation and the switch-off.
    /// </summary>
    [Fact]
    public async Task PutAutopilot_Disable_RevokesWithDisabledAndAudits()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: true);
        using var factory = world.Factory;

        // No session: a disable must not be gated on a re-authentication (task §Design "enabled = false").
        using var client = Owner(world, reauthenticated: false);
        var tag = await TagAsync(client, world.SeriesId);

        var moved = DefaultSettings() with { Tone = "tat" };
        using var response = await PutAsync(client, world.SeriesId, enabled: false, moved, tag);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        Enabled(world.SeriesId).ShouldBeFalse();
        SqlProbe.ScalarText(
                Database,
                $"SELECT coalesce(revoked_reason, '') || '|' || (revoked_at IS NOT NULL)::text "
                + $"FROM series_autopilot_authorizations WHERE id = '{world.AuthorizationId}'")
            .ShouldBe("disabled|true");
        Pointer(world.SeriesId).ShouldBeNull();

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'autopilot.disabled' AND entity_id = '{world.SeriesId}'")
            .ShouldBe(1);

        // Plan §4's shape for the switch-off row: `seriesId, revokedAuthorizationId, reason`.
        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(data ->> 'revokedAuthorizationId', '') || '|' || coalesce(data ->> 'reason', '') "
                + "FROM audit_logs "
                + $"WHERE action = 'autopilot.disabled' AND entity_id = '{world.SeriesId}'")
            .ShouldBe($"{world.AuthorizationId}|disabled", "the row names the revoked row and the wire reason");

        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(string_agg(coalesce(data ->> 'reason', ''), ','), '') FROM audit_logs "
                + $"WHERE action = 'autopilot.authorization_revoked' AND entity_id = '{world.SeriesId}'")
            .ShouldBe("disabled", "the revocation row names the reason the switch-off used");

        // The settings still travel: a disable is an edit of the document as well as a switch.
        SettingsColumn(world.SeriesId, "tone").ShouldBe("tat");
        StoredSettingsAre(world.SeriesId, moved)
            .ShouldBeTrue("the whole document is saved with `enabled = false`, not only the switch");
    }

    /// <summary>
    /// <c>enabled = false</c> on a series that is already off is a document edit, not a second switch-off: the
    /// document is applied and a field the accepted text never names keeps the consent the owner gave while the switch
    /// was off (plan §3.7 checks the covered fields at <em>every</em> settings write).
    /// </summary>
    [Fact]
    public async Task PutAutopilot_SettingsOnlyWhileDisabled_AppliesAndKeepsTheConsent()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        var tag = await TagAsync(client, world.SeriesId);
        var moved = DefaultSettings() with { Tone = "tat" };

        using var response = await PutAsync(client, world.SeriesId, enabled: false, moved, tag);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        Enabled(world.SeriesId).ShouldBeFalse("the flag was already off and the request said off");
        StoredSettingsAre(world.SeriesId, moved).ShouldBeTrue("the document is applied with the switch off");

        SqlProbe.ScalarText(Database, AuthorizationColumn(world, "(revoked_at IS NOT NULL)::text"))
            .ShouldBe("false", "tone is not part of the accepted text");
        Pointer(world.SeriesId).ShouldBe(
            world.AuthorizationId,
            "a non-covered edit never spends an acceptance given while the switch was off");

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'autopilot.disabled' AND entity_id = '{world.SeriesId}'")
            .ShouldBe(0, "nothing was switched off, so no switch-off row is written");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'autopilot.authorization_revoked' "
                + $"AND entity_id = '{world.SeriesId}'")
            .ShouldBe(0, "no covered field moved");
    }

    /// <summary>
    /// The document and the switch both already say what the request says, so the write changes nothing: the series
    /// row keeps its <c>xmin</c> and the tag the client sent stays valid (README D9, and
    /// <c>Series.DisableAutopilot</c>'s own idempotence remark). A store that re-stamped an identical document would
    /// answer with a new tag for a no-op and make every retry look like a lost update.
    /// </summary>
    /// <remarks>
    /// <c>series.autopilot_settings</c> is <c>jsonb</c>, so the stored text is PostgreSQL's rendering of the document
    /// and never byte-equal to <c>Serialize</c>'s canonical text; this case is what proves the comparison is made
    /// against the document rather than against its spelling.
    /// </remarks>
    [Fact]
    public async Task PutAutopilot_IdenticalDisableWhileOff_KeepsTheTagAndWritesNothing()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        var tag = await TagAsync(client, world.SeriesId);

        using var response = await PutAsync(client, world.SeriesId, enabled: false, DefaultSettings(), tag);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        using var document = JsonDocument.Parse(text);
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.ToString().ShouldBe(tag, "no column changed, so the tag is the one the client held");
        document.RootElement.GetProperty("etag").GetString().ShouldBe(tag, "the body carries the same value");

        Enabled(world.SeriesId).ShouldBeFalse();
        Pointer(world.SeriesId).ShouldBe(world.AuthorizationId, "the document is identical, so no consent was spent");
        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM audit_logs "
                + $"WHERE entity_id = '{world.SeriesId}' AND action LIKE 'autopilot.%'")
            .ShouldBe(0, "nothing moved, so nothing is audited");
    }

    /// <summary>
    /// Task §Failure handling "series archived while enabled": the archive disables the autopilot and revokes its
    /// consent in the same transaction, and <c>series.archived</c> — one row per command (BD15) — carries the revoked
    /// row's id (archive handler of 09.4.1-T1 plus 02.8.3 plan §3.7).
    /// </summary>
    [Fact]
    public async Task ArchiveSeries_WhileEnabled_DisablesAndRevokes()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: true);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        using var response = await client.SendAsync(
            Request(HttpMethod.Delete.Method, $"{SeriesRoute}/{world.SeriesId}"),
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        SqlProbe.ScalarText(
                Database,
                $"SELECT (archived_at IS NOT NULL)::text || '|' || autopilot_enabled::text FROM series WHERE id = '{world.SeriesId}'")
            .ShouldBe("true|false", "the archive disables the autopilot (plan §3.7 [DECISION])");
        Pointer(world.SeriesId).ShouldBeNull();

        SqlProbe.ScalarText(
                Database,
                $"SELECT coalesce(revoked_reason, '') || '|' || (revoked_at IS NOT NULL)::text "
                + $"FROM series_autopilot_authorizations WHERE id = '{world.AuthorizationId}'")
            .ShouldBe("disabled|true");

        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(string_agg(coalesce(data ->> 'authorizationId', '') || '|' "
                + "|| coalesce(data ->> 'reason', ''), ','), '') FROM audit_logs "
                + $"WHERE action = 'series.archived' AND entity_id = '{world.SeriesId}'")
            .ShouldBe($"{world.AuthorizationId}|disabled", "one row records both facts");
    }

    /// <summary>
    /// S77 review C-3/S-2 (route gate): an archived series can never be switched on again — 409 <c>series_archived</c>
    /// from the enable branch, above the ladder and after the <c>If-Match</c> check — while the document of that
    /// switched-off series stays editable and a disable still answers 200. The archive route itself has already
    /// disabled autopilot, so this only stops a re-enable from re-creating a live consent pointer on a dead series.
    /// </summary>
    [Fact]
    public async Task PutAutopilot_EnableOnArchivedSeries_Returns409AndTheOffEditStaysAllowed()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, authorized: true, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        using (var archive = await client.SendAsync(
            Request(HttpMethod.Delete.Method, $"{SeriesRoute}/{world.SeriesId}"),
            Ct))
        {
            archive.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                await archive.Content.ReadAsStringAsync(Ct));
        }

        var settings = DefaultSettings();
        var tag = await TagAsync(client, world.SeriesId);

        using (var enable = await PutAsync(client, world.SeriesId, enabled: true, settings, tag))
        {
            var problem = await ProblemAsync(enable);
            enable.StatusCode.ShouldBe(HttpStatusCode.Conflict, "an archived series is never enabled (plan §3.7)");
            problem.Code.ShouldBe("series_archived");
        }

        Enabled(world.SeriesId).ShouldBeFalse("the refusal writes nothing");

        // The document of a switched-off series is still the owner's to edit (the same 200 the settings-only path
        // answers), because it changes no switch and revokes no consent: the archive already revoked it.
        var edited = Move(settings, "tone");
        var retag = await TagAsync(client, world.SeriesId);

        using (var response = await PutAsync(client, world.SeriesId, enabled: false, edited, retag))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }

        StoredSettingsAre(world.SeriesId, edited).ShouldBeTrue(
            "the settings-only edit is not part of the archive gate");
    }

    /// <summary>The settings every test starts from (19:00 Mon/Thu, daily_cap 1, public, 12 h veto).</summary>
    /// <returns>The suite's default document.</returns>
    private static AutopilotSettings DefaultSettings() => AutopilotTickHarness.Settings(daysOfWeek: Days);

    /// <summary>
    /// Moves exactly one member of <paramref name="settings"/> to another valid value, so a case's moved field is
    /// the only difference the comparator can see.
    /// </summary>
    /// <param name="settings">The document the authorization was accepted for.</param>
    /// <param name="field">The member to move.</param>
    /// <returns>The moved document.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="field"/> is not a case of this suite.</exception>
    private static AutopilotSettings Move(AutopilotSettings settings, string field) => field switch
    {
        "cadence.publish_time" => settings with { Cadence = settings.Cadence with { PublishTime = new TimeOnly(20, 0) } },
        "cadence.days_of_week" => settings with { Cadence = settings.Cadence with { DaysOfWeek = [DayOfWeek.Monday] } },
        "cadence.time_zone" => settings with { Cadence = settings.Cadence with { TimeZoneId = "Asia/Tokyo" } },
        "daily_cap" => settings with { DailyCap = 2 },
        "privacy" => settings with { Privacy = PrivacyStatus.Unlisted },
        "veto_window_hours" => settings with { VetoWindowHours = 24 },
        "pre_audit_mode" => settings with { PreAuditMode = PreAuditMode.PauseAfterNPrivate },
        "tone" => settings with { Tone = "am ap, cham rai" },
        "banned_themes" => settings with { BannedThemes = ["bao luc"] },
        "gate_thresholds_ref" => settings with { GateThresholdsRef = "d5" },
        "render_lead_minutes" => settings with { RenderLeadMinutes = 120 },
        "story_lead_minutes" => settings with { StoryLeadMinutes = 240 },
        "buffer_days" => settings with { BufferDays = 3 },
        "pre_audit_pause_after" => settings with { PreAuditPauseAfter = 2 },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "No value is declared for this case."),
    };

    private static string Url(Guid seriesId) =>
        $"{SeriesRoute}/{seriesId.ToString("D", CultureInfo.InvariantCulture)}/autopilot";

    /// <summary>The owner's client: with a session family when the request needs a re-authentication.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="reauthenticated">Whether the client carries a recently re-authenticated family.</param>
    /// <returns>The client the test sends through.</returns>
    private static HttpClient Owner(World world, bool reauthenticated) => reauthenticated
        ? world.Factory.ClientFor(world.User.Id, world.User.Role, sessionId: world.FamilyId)
        : world.Factory.ClientFor(world.User.Id, world.User.Role);

    private static HttpRequestMessage Request(string method, string url, string? body = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        Guid seriesId,
        bool enabled,
        AutopilotSettings settings,
        string? ifMatch) =>
        PutAsync(client, seriesId, enabled, AutopilotSettingsValidator.Serialize(settings), ifMatch);

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        Guid seriesId,
        bool enabled,
        string settingsJson,
        string? ifMatch)
    {
        var body = "{\"enabled\":" + (enabled ? "true" : "false") + ",\"settings\":" + settingsJson + "}";

        return client.SendAsync(Request(HttpMethod.Put.Method, Url(seriesId), body, ifMatch), Ct);
    }

    private static async Task<string> TagAsync(HttpClient client, Guid seriesId)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get.Method, Url(seriesId)), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        response.Headers.ETag.ShouldNotBeNull("the read publishes the tag the write demands");

        return response.Headers.ETag!.ToString();
    }

    private static async Task<ProblemBody> ProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = document.RootElement;

        Dictionary<string, string[]>? errors = null;
        if (root.TryGetProperty("errors", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                errors[property.Name] = [.. property.Value.EnumerateArray()
                    .Select(value => value.GetString() ?? string.Empty)];
            }
        }

        return new ProblemBody(root.GetProperty("code").GetString()!, errors);
    }

    /// <summary>Satisfies the 13.2.1-T1 gate (0) through the real route, with the host's own document versions.</summary>
    /// <param name="factory">The host, for the configured versions.</param>
    /// <param name="client">The owner's client.</param>
    private static async Task AcceptLegalAsync(ApiFactory factory, HttpClient client)
    {
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        using var response = await client.PostAsJsonAsync(
            LegalAcceptRoute,
            new
            {
                tos = configuration["LegalDocuments:Current:Tos"] ?? "1.0.0",
                privacy = configuration["LegalDocuments:Current:Privacy"] ?? "1.0.0",
            },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the legal gate must be satisfied from real rows");
    }

    /// <summary>Whether <c>series.autopilot_enabled</c> is set.</summary>
    /// <param name="seriesId">The series.</param>
    /// <returns>The column's value.</returns>
    private bool Enabled(Guid seriesId) =>
        SqlProbe.ScalarText(Database, $"SELECT autopilot_enabled::text FROM series WHERE id = '{seriesId}'") == "true";

    /// <summary>One top-level member of the stored <c>series.autopilot_settings</c> document, as text.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="member">The JSON member.</param>
    /// <returns>The member's text value.</returns>
    private string SettingsColumn(Guid seriesId, string member) => SqlProbe.ScalarText(
        Database,
        $"SELECT coalesce(autopilot_settings ->> '{member}', '') FROM series WHERE id = '{seriesId}'");

    /// <summary>
    /// Whether the stored <c>series.autopilot_settings</c> document is <paramref name="expected"/>, compared as
    /// <c>jsonb</c> so the column's own key order cannot make two equal documents differ.
    /// </summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="expected">The document the request wrote.</param>
    /// <returns>Whether the two documents are equal.</returns>
    private bool StoredSettingsAre(Guid seriesId, AutopilotSettings expected)
    {
        var json = AutopilotSettingsValidator.Serialize(expected).Replace("'", "''", StringComparison.Ordinal);

        return SqlProbe.ScalarText(
            Database,
            $"SELECT (autopilot_settings = '{json}'::jsonb)::text FROM series WHERE id = '{seriesId}'") == "true";
    }

    /// <summary>The series' current authorization pointer, or null when it is clear.</summary>
    /// <param name="seriesId">The series.</param>
    /// <returns>The pointer as a nullable id.</returns>
    private Guid? Pointer(Guid seriesId)
    {
        var text = SqlProbe.ScalarText(
            Database,
            $"SELECT coalesce(current_autopilot_authorization_id::text, '') FROM series WHERE id = '{seriesId}'");

        return string.IsNullOrEmpty(text) ? null : Guid.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>One expression over the seeded authorization row, which every case names by its id.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="expression">The SQL expression to select (for example <c>coalesce(revoked_reason, '')</c>).</param>
    /// <returns>The expression's text value.</returns>
    private string AuthorizationColumn(World world, string expression) => SqlProbe.ScalarText(
        Database,
        $"SELECT {expression} FROM series_autopilot_authorizations WHERE id = '{world.AuthorizationId}'");

    /// <summary>
    /// Inserts one accepted authorization row through the aggregate's own factory, with the covered snapshot the
    /// settings and the audit bit produce.
    /// </summary>
    /// <param name="userId">The owner.</param>
    /// <param name="seriesId">The series.</param>
    /// <param name="channelId">The channel the text named.</param>
    /// <param name="topic">The topic at acceptance.</param>
    /// <param name="settings">The settings the text was rendered from.</param>
    /// <param name="preAuditPending">The audit bit the text carried.</param>
    /// <returns>The new row's id.</returns>
    private async Task<Guid> InsertAuthorizationAsync(
        Guid userId,
        Guid seriesId,
        Guid channelId,
        Topic topic,
        AutopilotSettings settings,
        bool preAuditPending)
    {
        await using var context = Database.CreateAppDbContext(
            new TestCurrentUser { Id = userId },
            new FakeClock(Start));

        var row = SeriesAutopilotAuthorization.Accept(
            userId,
            seriesId,
            version: 1,
            textVersion: AuthorizationTextRenderer.CurrentTextVersion,
            textHash: new string('a', 64),
            topic: topic,
            channelConnectionId: channelId,
            snapshotJson: AutopilotAuthorizationCoverage.SnapshotJson(settings, preAuditPending),
            privacy: settings.Privacy,
            now: Start);

        context.SeriesAutopilotAuthorizations.Add(row);
        await context.SaveChangesAsync(Ct);

        return row.Id;
    }

    /// <summary>
    /// Seeds one owner with a live (and freshly re-authenticated) session family, the flag row, one channel
    /// connection, one series with a settings document and — when asked — an accepted authorization pointing at it.
    /// </summary>
    /// <param name="clock">The clock the host and every row share.</param>
    /// <param name="authorized">Whether the series has a current authorization.</param>
    /// <param name="enabled">The <c>autopilot_enabled</c> column.</param>
    /// <param name="settings">The series' settings document; the suite's default when omitted.</param>
    /// <param name="snapshotSettings">The settings the seeded authorization covers; <paramref name="settings"/> when omitted.</param>
    /// <param name="snapshotPreAuditPending">The audit bit the seeded snapshot carries (the host answers pending).</param>
    /// <param name="topic">The series' topic now.</param>
    /// <param name="authorizedTopic">The topic the authorization names; <paramref name="topic"/> when omitted.</param>
    /// <param name="channelStatus">The seeded connection's status.</param>
    /// <returns>The seeded world, whose factory the test disposes.</returns>
    private async Task<World> SeedAsync(
        FakeClock clock,
        bool authorized,
        bool enabled,
        AutopilotSettings? settings = null,
        AutopilotSettings? snapshotSettings = null,
        bool snapshotPreAuditPending = true,
        Topic topic = Topic.Ghost,
        Topic? authorizedTopic = null,
        ChannelConnectionStatus channelStatus = ChannelConnectionStatus.Active)
    {
        // The flag row has to exist before the host (and its cached flags) does.
        await Database.ExecuteAsync($"DELETE FROM feature_flags WHERE key = '{FeatureKey}'", Ct);
        await Database.ExecuteAsync(
            "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
            + $"VALUES ('{FeatureKey}', true, '{{}}', now(), now())",
            Ct);

        var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        var actor = new TestCurrentUser { Id = user.Id };
        var channel = await new ChannelConnectionBuilder(Database, actor, clock)
            .CreateAsync(user.Id, channelStatus, ct: Ct);

        var document = settings ?? DefaultSettings();
        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            user.Id,
            topic: topic,
            autopilotEnabled: enabled,
            autopilotSettings: AutopilotSettingsValidator.Serialize(document),
            ct: Ct);

        var authorizationId = authorized
            ? await InsertAuthorizationAsync(
                user.Id,
                series.Id,
                channel.Id,
                authorizedTopic ?? topic,
                snapshotSettings ?? document,
                snapshotPreAuditPending)
            : (Guid?)null;

        await Database.ExecuteAsync(
            "UPDATE series SET "
            + $"default_channel_id = '{channel.Id}', "
            + "current_autopilot_authorization_id = "
            + (authorizationId is { } id ? $"'{id}'" : "NULL")
            + $" WHERE id = '{series.Id}'",
            Ct);

        return new World(factory, user, familyId, series.Id, channel.Id, authorizationId);
    }

    /// <summary>One seeded owner, its session family, its series and its optional authorization.</summary>
    /// <param name="Factory">The host the world was seeded into.</param>
    /// <param name="User">The owner.</param>
    /// <param name="FamilyId">The live refresh family, re-authenticated at the clock's reading.</param>
    /// <param name="SeriesId">The series.</param>
    /// <param name="ChannelId">The series' default channel connection.</param>
    /// <param name="AuthorizationId">The current authorization, or null when the seed has none.</param>
    private sealed record World(
        ApiFactory Factory,
        AppUser User,
        Guid FamilyId,
        Guid SeriesId,
        Guid ChannelId,
        Guid? AuthorizationId);

    /// <summary>The two members of a problem response this suite asserts.</summary>
    /// <param name="Code">The stable code.</param>
    /// <param name="Errors">The field groups, or null when the body carries none.</param>
    private sealed record ProblemBody(string Code, Dictionary<string, string[]>? Errors);
}
