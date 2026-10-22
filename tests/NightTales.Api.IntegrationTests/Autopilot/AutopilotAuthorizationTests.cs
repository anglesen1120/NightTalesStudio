using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Autopilot.Authorization;
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
/// The consent half of the autopilot wire contract (02.8.3-T2 task §Tests rows <c>AcceptAuthorization_*</c>,
/// <c>RevokeAuthorization_*</c>, <c>CoveredFieldChange_MovesPlannedRunsToAwaitingAuthorization</c> and
/// <c>AuditRows_*</c>): the versioned text accept, its stale-hash and stale-version refusals, the replay, the
/// supersede, the owner's revoke and the run that waits for the new text.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hash every accept sends is the one the page rendered</b>: the suite reads
/// <c>GET /series/{id}/autopilot?locale=en</c> and posts <c>authorizationText.{textVersion,textHash}</c> back, which
/// is the flow 10.4.3-T1's dialog runs. Hand-computing a digest would let a renderer change the sentence without any
/// test noticing that the dialog can no longer accept what it displayed.
/// </para>
/// <para>
/// <b>Rows and events, not just statuses.</b> An acceptance is only real when the row, the pointer, the audit entry
/// and the <c>autopilot_authorization_accepted.v1</c> outbox row all exist (plan §3.7, §4); each case asserts the four
/// facts it changes and the ones it must not.
/// </para>
/// <para>
/// <b>One recorded gap.</b> <c>revoked_reason</c> is mapped but has no migration in this worktree yet (S77 contract
/// D-1), so the reads of that column are red until the orchestrator generates it.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
public sealed class AutopilotAuthorizationTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The series route prefix of 09 §5.</summary>
    private const string SeriesRoute = "/api/v1/series";

    /// <summary>06 README D9: the flag the series routes carry.</summary>
    private const string FeatureKey = "youtube_publish";

    /// <summary>The legal-acceptance route the accept ladder's gate (0) reads (13.2.1-T1).</summary>
    private const string LegalAcceptRoute = "/api/v1/me/legal/accept";

    /// <summary>The outbox type the acceptance raises (S71-7: the event exists so a waiting run can continue).</summary>
    private const string AcceptedEventType = "autopilot_authorization_accepted.v1";

    /// <summary>The instant every test's clock is pinned to: a Thursday, with the next Mon/Thu slot ahead of it.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The cadence every seeded series starts from: Monday and Thursday at 19:00, Ho Chi Minh time.</summary>
    private static readonly DayOfWeek[] Days = [DayOfWeek.Monday, DayOfWeek.Thursday];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Task §Tests <c>AcceptAuthorization_HappyPath_WritesRowAndAudit</c> and §Acceptance 4: 201 with the version and
    /// the instant, the pointer set, the stored digest equal to the one the page rendered, an
    /// <c>autopilot.authorization_accepted</c> row naming that digest, and the event row a waiting run is woken by.
    /// </summary>
    [Fact]
    public async Task AcceptAuthorization_HappyPath_WritesRowAuditAndOutboxEvent()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var rendered = await RenderedTextAsync(client, world.SeriesId);
        using var response = await AcceptAsync(client, world.SeriesId, rendered.Version, rendered.Hash);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, text);

        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
        document.RootElement.TryGetProperty("acceptedAt", out _).ShouldBeTrue("the dialog needs the instant it shows");

        var authorizationId = Pointer(world.SeriesId);
        authorizationId.ShouldNotBeNull("an acceptance makes the new revision current (plan §3.7)");

        SqlProbe.ScalarText(
                Database,
                $"SELECT text_hash || '|' || version::text || '|' || (revoked_at IS NULL)::text "
                + $"FROM series_autopilot_authorizations WHERE id = '{authorizationId}'")
            .ShouldBe($"{rendered.Hash}|1|true");

        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(string_agg(coalesce(data ->> 'textHash', '') || '|' "
                + "|| coalesce(data ->> 'authorizationId', ''), ','), '') FROM audit_logs "
                + $"WHERE action = 'autopilot.authorization_accepted' AND entity_id = '{world.SeriesId}'")
            .ShouldBe(
                $"{rendered.Hash}|{authorizationId}",
                "the audit row names the digest the owner accepted (task §Acceptance 4)");

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = '{AcceptedEventType}' "
                + $"AND user_id = '{world.User.Id}'")
            .ShouldBe(1, "the acceptance is an outbox event so a waiting run can leave its state (S71-7)");
        SqlProbe.ScalarText(
                Database,
                "SELECT payload::text FROM outbox_messages "
                + $"WHERE type = '{AcceptedEventType}' AND user_id = '{world.User.Id}'")
            .ShouldContain(rendered.Hash, Case.Sensitive, "the event carries the accepted digest");

        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM series_autopilot_authorizations "
                + $"WHERE series_id = '{world.SeriesId}'")
            .ShouldBe(1, "a first acceptance writes exactly one revision");
    }

    /// <summary>
    /// Task §Tests <c>AcceptAuthorization_NoReauth_Returns401</c>: the acceptance authorizes every future upload, so
    /// the caller must have re-authenticated within five minutes (BD4).
    /// </summary>
    [Fact]
    public async Task AcceptAuthorization_NoReauth_Returns401()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);
        await AcceptLegalAsync(factory, client);

        var rendered = await RenderedTextAsync(client, world.SeriesId);
        using var response = await AcceptAsync(client, world.SeriesId, rendered.Version, rendered.Hash);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemAsync(response)).Code.ShouldBe("reauth_required");
        Pointer(world.SeriesId).ShouldBeNull();
        Authorizations(world.SeriesId).ShouldBe(0);
    }

    /// <summary>
    /// Task §Acceptance 2 and §Tests <c>AcceptAuthorization_StaleHash_Returns409</c>: a page rendered before a
    /// settings change carries a digest this server no longer produces, and the 409 says exactly which field is stale.
    /// </summary>
    [Fact]
    public async Task AcceptAuthorization_StaleHash_Returns409WithStaleText()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var rendered = await RenderedTextAsync(client, world.SeriesId);

        // The settings move behind the page's back, as a PUT would move them.
        await Database.ExecuteAsync(
            "UPDATE series SET autopilot_settings = jsonb_set(autopilot_settings, '{daily_cap}', '2') "
            + $"WHERE id = '{world.SeriesId}'",
            Ct);

        using var response = await AcceptAsync(client, world.SeriesId, rendered.Version, rendered.Hash);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ProblemAsync(response);
        problem.Code.ShouldBe("autopilot_authorization_required");
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("textHash");
        problem.Errors["textHash"].ShouldHaveSingleItem().ShouldBe("stale_text");
        Authorizations(world.SeriesId).ShouldBe(0, "a refused acceptance writes no row");
    }

    /// <summary>
    /// Task §Failure handling: a page from an older template version is refused with <c>errors.textVersion =
    /// "stale_version"</c>, so the client can tell the two staleness kinds apart and re-read what it needs.
    /// </summary>
    [Fact]
    public async Task AcceptAuthorization_StaleVersion_Returns409WithStaleVersion()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var rendered = await RenderedTextAsync(client, world.SeriesId);
        using var response = await AcceptAsync(client, world.SeriesId, "autopilot-auth.v0", rendered.Hash);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ProblemAsync(response);
        problem.Code.ShouldBe("autopilot_authorization_required");
        problem.Errors!.ShouldContainKey("textVersion");
        problem.Errors!["textVersion"].ShouldHaveSingleItem().ShouldBe("stale_version");
        Authorizations(world.SeriesId).ShouldBe(0);
    }

    /// <summary>
    /// Task §Tests <c>AcceptAuthorization_Replay_IsIdempotent</c>: accepting the same text twice is one acceptance —
    /// the same version comes back, with no second row, no second audit entry and no second event (the contract's
    /// "a replay is not a new version").
    /// </summary>
    [Fact]
    public async Task AcceptAuthorization_Replay_IsIdempotent()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var rendered = await RenderedTextAsync(client, world.SeriesId);

        using var first = await AcceptAsync(client, world.SeriesId, rendered.Version, rendered.Hash);
        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync(Ct));
        var firstId = Pointer(world.SeriesId);

        using var replay = await AcceptAsync(client, world.SeriesId, rendered.Version, rendered.Hash);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created, await replay.Content.ReadAsStringAsync(Ct));

        using var document = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("version").GetInt32().ShouldBe(1, "the replay is the same acceptance");
        Pointer(world.SeriesId).ShouldBe(firstId);
        Authorizations(world.SeriesId).ShouldBe(1);
        AuditCount("autopilot.authorization_accepted", world.SeriesId).ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = '{AcceptedEventType}' "
                + $"AND user_id = '{world.User.Id}'")
            .ShouldBe(1, "a replay raises nothing");
    }

    /// <summary>
    /// Task §Tests <c>AcceptAuthorization_SecondTime_IncrementsVersion</c> and the supersede row: when the rendered
    /// text changes without a covered field moving — the channel's display name is part of the sentence and is not
    /// in the coverage snapshot — the new acceptance takes version 2 and revokes version 1 with
    /// <c>superseded</c>, because a series never has two live authorizations (plan §3.7).
    /// </summary>
    [Fact]
    public async Task AcceptAuthorization_SecondTime_SupersedesThePreviousRevision()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: false);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        var first = await RenderedTextAsync(client, world.SeriesId);
        using var accepted = await AcceptAsync(client, world.SeriesId, first.Version, first.Hash);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync(Ct));
        var previousId = Pointer(world.SeriesId);

        // The channel is renamed: the sentence names it, but the coverage snapshot compares the connection id.
        await Database.ExecuteAsync(
            "UPDATE channel_connections SET display_name = 'Ke chuyen dem khuya' "
            + $"WHERE id = '{world.ChannelId}'",
            Ct);

        var second = await RenderedTextAsync(client, world.SeriesId);
        second.Hash.ShouldNotBe(first.Hash, "the sentence really changed");
        second.Version.ShouldBe(first.Version, "the template version did not");

        using var response = await AcceptAsync(client, world.SeriesId, second.Version, second.Hash);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("version").GetInt32().ShouldBe(2);
        Pointer(world.SeriesId).ShouldNotBe(previousId, "the new revision is current");

        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(revoked_reason, '') || '|' || (revoked_at IS NOT NULL)::text "
                + $"FROM series_autopilot_authorizations WHERE id = '{previousId}'")
            .ShouldBe("superseded|true", "the previous revision is superseded, not deleted (plan §3.7)");
        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(string_agg(coalesce(data ->> 'reason', ''), ','), '') FROM audit_logs "
                + "WHERE action = 'autopilot.authorization_revoked' "
                + $"AND entity_id = '{world.SeriesId}'")
            .ShouldBe("superseded", "the supersede is audited with the wire reason");
        Authorizations(world.SeriesId).ShouldBe(2);
    }

    /// <summary>
    /// Task §Tests <c>RevokeAuthorization_Endpoint_SetsRevokedAtAndReason</c>: 204, the current revision revoked as
    /// <c>owner_revoked</c>, the pointer cleared, the switch still on (plan §3.7: a revoke stops uploads, it does not
    /// change the series' setting) and one audit row.
    /// </summary>
    [Fact]
    public async Task RevokeAuthorization_SetsRevokedAtAndReason()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: true);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        using var response = await client.SendAsync(
            Request(HttpMethod.Delete.Method, AuthorizationUrl(world.SeriesId)),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(revoked_reason, '') || '|' || (revoked_at IS NOT NULL)::text "
                + $"FROM series_autopilot_authorizations WHERE id = '{world.AuthorizationId}'")
            .ShouldBe("owner_revoked|true");
        Pointer(world.SeriesId).ShouldBeNull();
        Enabled(world.SeriesId).ShouldBeTrue("a revoke leaves autopilot enabled; runs simply wait (plan §3.7)");

        AuditCount("autopilot.authorization_revoked", world.SeriesId).ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(string_agg(coalesce(data ->> 'reason', '') || '|' "
                + "|| coalesce(data ->> 'authorizationId', ''), ','), '') FROM audit_logs "
                + "WHERE action = 'autopilot.authorization_revoked' "
                + $"AND entity_id = '{world.SeriesId}'")
            .ShouldBe($"owner_revoked|{world.AuthorizationId}");

        using var read = await client.SendAsync(Request(HttpMethod.Get.Method, Url(world.SeriesId)), Ct);
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("authorization").ValueKind.ShouldBe(JsonValueKind.Null);
        document.RootElement.GetProperty("requiredAuthorization").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// Task §Tests <c>RevokeAuthorization_WithNothingCurrent_IsIdempotent</c>: the documented window — a retried
    /// DELETE, or a DELETE after an automatic revocation — answers 204 again and writes nothing, because the caller's
    /// intent already holds.
    /// </summary>
    [Fact]
    public async Task RevokeAuthorization_WithNothingCurrent_IsIdempotent()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: true);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: false);

        using var first = await client.SendAsync(Request(HttpMethod.Delete.Method, AuthorizationUrl(world.SeriesId)), Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var second = await client.SendAsync(Request(HttpMethod.Delete.Method, AuthorizationUrl(world.SeriesId)), Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AuditCount("autopilot.authorization_revoked", world.SeriesId).ShouldBe(1, "the second call is a no-op");
    }

    /// <summary>
    /// Task §Tests <c>AcceptAuthorization_ForeignSeries_Returns404NotReauthChallenge</c>: ownership is decided before
    /// the consent gates, so a series the caller does not own is a 404 and never a re-authentication prompt (plan
    /// §3.7 "ungated beyond authentication and ownership").
    /// </summary>
    [Fact]
    public async Task AcceptAuthorization_ForeignSeries_Returns404NotReauthChallenge()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: false);
        using var factory = world.Factory;
        var intruder = await AuthTestFlow.CreateUserAsync(factory.Services);

        // The intruder has no session at all: a 404 proves ownership ran before the re-auth gate.
        using var client = factory.ClientFor(intruder.Id, intruder.Role);

        using var response = await client.SendAsync(
            Request(HttpMethod.Post.Method, AuthorizationUrl(world.SeriesId), AcceptBody("autopilot-auth.v1", new string('a', 64))),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Authorizations(world.SeriesId).ShouldBe(0);
    }

    /// <summary>
    /// Task §Tests <c>AuditRows_NeverCarryTheAuthorizationText</c> and D13: the four autopilot rows of one series name
    /// ids, versions, digests and codes — the accepted sentence itself, which is the owner's consent text, is never
    /// copied into an audit entry (and a digest is not the text).
    /// </summary>
    [Fact]
    public async Task AuditRows_NameIdsAndDigestsButNeverTheText()
    {
        var clock = new FakeClock(Start);
        var world = await SeedAsync(clock, enabled: true);
        using var factory = world.Factory;
        using var client = Owner(world, reauthenticated: true);
        await AcceptLegalAsync(factory, client);

        // Enable (audited), then accept, then revoke: the three rows plan §4 names for a series' consent.
        var tag = await TagAsync(client, world.SeriesId);
        using var enabled = await client.SendAsync(
            Request(HttpMethod.Put.Method, Url(world.SeriesId), "{\"enabled\":true,\"settings\":"
                + AutopilotSettingsValidator.Serialize(AutopilotTickHarness.Settings(daysOfWeek: Days)) + "}", tag),
            Ct);
        enabled.StatusCode.ShouldBe(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync(Ct));

        var rendered = await RenderedTextAsync(client, world.SeriesId);
        using var accepted = await AcceptAsync(client, world.SeriesId, rendered.Version, rendered.Hash);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync(Ct));

        using var revoked = await client.SendAsync(
            Request(HttpMethod.Delete.Method, AuthorizationUrl(world.SeriesId)),
            Ct);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, await revoked.Content.ReadAsStringAsync(Ct));

        var rows = await Database.QueryStringsAsync(
            "SELECT action || '=' || coalesce(data::text, '') FROM audit_logs "
            + $"WHERE entity_type = 'series' AND entity_id = '{world.SeriesId}' ORDER BY action",
            Ct);

        rows.ShouldNotBeEmpty("the three commands wrote their rows");

        // The sentence is what must not leak: a 40-character span of it is unique enough to find a copy of any part.
        var phrase = rendered.Text[..40];
        foreach (var row in rows)
        {
            row.ShouldNotContain(phrase, Case.Sensitive, $"an audit row copied the authorization text: {row}");
            row.ShouldNotContain("{{", Case.Sensitive);
        }

        var acceptedRow = rows.Single(row => row.StartsWith("autopilot.authorization_accepted=", StringComparison.Ordinal));
        acceptedRow.ShouldContain(rendered.Hash, Case.Sensitive, "the digest is the reference the audit keeps");
        acceptedRow.ShouldNotContain("\"text\":", Case.Sensitive, "only `textHash` is allowed, never the text");
    }

    /// <summary>
    /// Task §Acceptance 5's run-side half (S77 contract line "covered change → planned runs stop"): once the covered
    /// change has revoked the authorization (the settings suite asserts that half through <c>PUT</c>), the next advance
    /// of a <c>planned</c> run stops it in <c>awaiting_authorization</c> at step <c>authorization</c> — while a paused
    /// run is untouched, because a pause is not the authorization's business (plan §3.5, §3.7).
    /// </summary>
    /// <remarks>
    /// The advance runs through the real orchestrator and its real step handlers (<c>AutopilotTickHarness</c> wires the
    /// paid seams to scripted fakes). The revocation here is the domain call the <c>PUT</c> handler makes — the handler
    /// path itself is asserted end to end in <see cref="AutopilotSettingsTests"/> — so this case stays about the run.
    /// </remarks>
    [Fact]
    public async Task CoveredFieldChange_MovesPlannedRunsToAwaitingAuthorization()
    {
        var clock = new FakeClock(Start);
        await using var harness = await AutopilotTickHarness.StartAsync(Database, clock, ct: Ct);

        var owner = await harness.CreateOwnerAsync(Ct);
        await harness.CreateAuthorizedSeriesAsync(owner, ct: Ct);

        var seriesId = Guid.Parse(
            (await harness.Database.QueryStringsAsync(
                $"SELECT id::text FROM series WHERE user_id = '{owner}'",
                Ct))[0],
            CultureInfo.InvariantCulture);
        var authorizationId = Guid.Parse(
            (await harness.Database.QueryStringsAsync(
                $"SELECT id::text FROM series_autopilot_authorizations WHERE series_id = '{seriesId}'",
                Ct))[0],
            CultureInfo.InvariantCulture);

        var planned = await harness.CreateRunAsync(
            owner,
            seriesId,
            slotAt: clock.UtcNow.AddDays(2),
            startAt: clock.UtcNow.AddHours(-1),
            authorizationId: authorizationId,
            state: AutopilotRunState.Planned,
            ct: Ct);

        var paused = await harness.CreateRunAsync(
            owner,
            seriesId,
            slotAt: clock.UtcNow.AddDays(3),
            startAt: clock.UtcNow.AddHours(-1),
            authorizationId: authorizationId,
            state: AutopilotRunState.Paused,
            pauseReason: AutopilotPauseReason.OwnerPaused,
            step: AutopilotRunStep.Idea,
            ct: Ct);

        // The covered change: the same two calls the PUT handler makes (revoke the live revision, clear the pointer).
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = owner }, clock))
        {
            var row = await db.SeriesAutopilotAuthorizations.SingleAsync(candidate => candidate.Id == authorizationId, Ct);
            row.Revoke(AutopilotAuthorizationRevokeReason.TextChanged, clock.UtcNow).ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }

        await harness.UpdateSeriesAsync(seriesId, "current_autopilot_authorization_id = NULL", Ct);

        var advanced = await harness.AdvanceAsync(planned.Id, owner, Ct);
        advanced.IsError.ShouldBeFalse(advanced.IsError ? advanced.FirstError.Description : string.Empty);

        var moved = await harness.RunAsync(owner, planned.Id, Ct);
        moved.State.ShouldBe(
            AutopilotRunState.AwaitingAuthorization,
            "a run may not spend on a slot whose text the owner has not accepted (plan §3.5)");
        moved.Step.ShouldBe(AutopilotRunStep.Authorization);
        moved.WaitingFor.ShouldBeNull(
            "waiting_for names an owner question inside waiting_owner; the authorization wait is the state itself");

        var stillPaused = await harness.RunAsync(owner, paused.Id, Ct);
        stillPaused.State.ShouldBe(AutopilotRunState.Paused, "a paused run keeps the reason the owner gave");
        stillPaused.PauseReason.ShouldBe(AutopilotPauseReason.OwnerPaused);
        stillPaused.Step.ShouldBe(AutopilotRunStep.Idea);
    }

    /// <summary>The settings every seeded series starts from (19:00 Mon/Thu, daily_cap 1, public, 12 h veto).</summary>
    /// <returns>The suite's default document.</returns>
    private static AutopilotSettings DefaultSettings() => AutopilotTickHarness.Settings(daysOfWeek: Days);

    private static string Url(Guid seriesId) =>
        $"{SeriesRoute}/{seriesId.ToString("D", CultureInfo.InvariantCulture)}/autopilot";

    private static string AuthorizationUrl(Guid seriesId) => $"{Url(seriesId)}/authorization";

    private static string AcceptBody(string textVersion, string textHash) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["textVersion"] = textVersion,
            ["textHash"] = textHash,
        });

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

    private static Task<HttpResponseMessage> AcceptAsync(HttpClient client, Guid seriesId, string version, string hash) =>
        client.SendAsync(
            Request(HttpMethod.Post.Method, AuthorizationUrl(seriesId), AcceptBody(version, hash)),
            Ct);

    private static async Task<Rendered> RenderedTextAsync(HttpClient client, Guid seriesId)
    {
        using var response = await client.SendAsync(
            Request(HttpMethod.Get.Method, $"{Url(seriesId)}?locale=en"),
            Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        using var document = JsonDocument.Parse(text);
        var rendered = document.RootElement.GetProperty("authorizationText");

        return new Rendered(
            rendered.GetProperty("textVersion").GetString()!,
            rendered.GetProperty("textHash").GetString()!,
            rendered.GetProperty("text").GetString()!);
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
                errors[property.Name] = [.. property.Value
                    .EnumerateArray()
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

    /// <summary>How many authorization revisions the series ever had.</summary>
    /// <param name="seriesId">The series.</param>
    /// <returns>The row count.</returns>
    private int Authorizations(Guid seriesId) => (int)SqlProbe.Count(
        Database,
        $"SELECT count(*) FROM series_autopilot_authorizations WHERE series_id = '{seriesId}'");

    /// <summary>How many audit rows one action has for the series.</summary>
    /// <param name="action">The action key.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The row count.</returns>
    private int AuditCount(string action, Guid seriesId) => (int)SqlProbe.Count(
        Database,
        $"SELECT count(*) FROM audit_logs WHERE action = '{action}' AND entity_id = '{seriesId}'");

    /// <summary>
    /// Seeds one owner with a live (and freshly re-authenticated) session family, the flag row, one channel connection
    /// and one series with a settings document and — when <paramref name="authorized"/> — an accepted revision.
    /// </summary>
    /// <param name="clock">The clock the host and every row share.</param>
    /// <param name="enabled">The <c>autopilot_enabled</c> column.</param>
    /// <param name="authorized">Whether the series starts with a current, live authorization.</param>
    /// <returns>The seeded world, whose factory the test disposes.</returns>
    private async Task<World> SeedAsync(FakeClock clock, bool enabled, bool authorized = false)
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
            .CreateAsync(user.Id, ChannelConnectionStatus.Active, ct: Ct);

        var settings = DefaultSettings();
        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            user.Id,
            topic: Topic.Ghost,
            autopilotEnabled: enabled,
            autopilotSettings: AutopilotSettingsValidator.Serialize(settings),
            ct: Ct);

        Guid? authorizationId = null;
        if (authorized)
        {
            await using var context = Database.CreateAppDbContext(actor, clock);
            var row = SeriesAutopilotAuthorization.Accept(
                user.Id,
                series.Id,
                version: 1,
                textVersion: AuthorizationTextRenderer.CurrentTextVersion,
                textHash: new string('a', 64),
                topic: Topic.Ghost,
                channelConnectionId: channel.Id,
                snapshotJson: AutopilotAuthorizationCoverage.SnapshotJson(settings, preAuditPending: true),
                privacy: settings.Privacy,
                now: clock.UtcNow);

            context.SeriesAutopilotAuthorizations.Add(row);
            await context.SaveChangesAsync(Ct);
            authorizationId = row.Id;
        }

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

    /// <summary>The three members of the rendered text block a case reads.</summary>
    /// <param name="Version">The template version.</param>
    /// <param name="Hash">The lowercase SHA-256 digest of the sentence.</param>
    /// <param name="Text">The rendered sentence itself.</param>
    private sealed record Rendered(string Version, string Hash, string Text);

    /// <summary>The two members of a problem response this suite asserts.</summary>
    /// <param name="Code">The stable code.</param>
    /// <param name="Errors">The field groups, or null when the body carries none.</param>
    private sealed record ProblemBody(string Code, Dictionary<string, string[]>? Errors);
}
