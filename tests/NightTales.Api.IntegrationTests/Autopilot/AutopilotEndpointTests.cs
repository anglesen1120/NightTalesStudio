using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Series.Autopilot;
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
/// The wire contract of the autopilot routes (02.8.3-T2 task §Routes/§Acceptance): the <c>youtube_publish</c> flag
/// hides every route, a foreign series is indistinguishable from a missing one, the read previews the scheduler's own
/// next three slots, and the run list walks its log page by page without a gap or a repeat.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here reaches a handler when it should not.</b> The flag-off and cross-tenant cases are asserted through
/// the real HTTP surface with an authenticated client, and both assert the problem's <c>code</c> and not only its
/// status: 404 means two very different things on this group (<c>feature_disabled</c> versus <c>not_found</c>) and a
/// status-only assertion would let a tenancy bypass pass as "the flag is off".
/// </para>
/// <para>
/// <b>One recorded deviation.</b> <c>ux_autopilot_runs_series_slot</c> is unique on <c>(series_id, slot_at)</c>, so two
/// runs of one series cannot share a <c>slot_at</c>; <see cref="ListAutopilotRuns_CursorPagination_IsStableOnEqualSlotAt"/>
/// proves the cursor's page-boundary properties on distinct instants and asserts the index as the evidence that the
/// equal-<c>slot_at</c> case the task row names is unreachable through the API.
/// </para>
/// <para>
/// Worktrees are not indexed by codebase-memory, so the helpers below were taken from
/// <c>Publishing/CancelPublicationTests.cs</c> (the flag row, <c>ApiFactory.ClientFor</c>, <c>SqlProbe</c>) and
/// <c>SeriesBible/SeriesBibleHarness.cs</c> (the response readers); the settings document comes from
/// <c>AutopilotTickHarness</c>'s static fixtures.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class AutopilotEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The route group of one series' autopilot (02.8.3 plan §5).</summary>
    private const string Route = "/api/v1/series";

    /// <summary>06 README D9: the flag every route of this group carries.</summary>
    private const string FeatureKey = "youtube_publish";

    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    /// <summary>Every route of the group, with the body each one needs to be a valid request.</summary>
    private static IEnumerable<(string Method, string Suffix, string? Body)> Routes()
    {
        yield return (HttpMethod.Get.Method, string.Empty, null);
        yield return (HttpMethod.Put.Method, string.Empty, """{"enabled":false,"settings":{}}""");
        yield return (HttpMethod.Post.Method, "/authorization", """{"textVersion":"autopilot-auth.v1","textHash":"abc"}""");
        yield return (HttpMethod.Delete.Method, "/authorization", null);
        yield return (HttpMethod.Post.Method, "/pause", "{}");
        yield return (HttpMethod.Post.Method, "/resume", null);
        yield return (HttpMethod.Get.Method, "/runs", null);
    }

    [Fact]
    public async Task AutopilotRoutes_FlagOff_Return404FeatureDisabled()
    {
        var world = await SeedAsync();
        await SetFlagAsync(false);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(world.UserId);

        // The series is the caller's own, so only the flag can answer 404 here: an owner who would otherwise be
        // allowed must still be refused, and with `feature_disabled` rather than `not_found`.
        foreach (var (method, suffix, body) in Routes())
        {
            using var request = Request(method, Url(world.SeriesId) + suffix, body, ifMatch: "\"sa-1\"");
            using var response = await client.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);

            response.StatusCode.ShouldBe(
                HttpStatusCode.NotFound,
                $"{method} {suffix} must be hidden by the flag: {text}");
            (await CodeAsync(response)).ShouldBe(
                "feature_disabled",
                $"{method} {suffix} must answer feature_disabled, not a tenancy answer");
        }
    }

    [Fact]
    public async Task CrossTenant_AutopilotRoutes_Return404()
    {
        var owner = await SeedAsync();
        var stranger = await SeedUserAsync();
        await SetFlagAsync(true);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(stranger);

        // 09 README D10: another tenant's series is indistinguishable from a missing one, on every route of the group
        // and whatever the body or the tag says — a 403 would confirm the id exists.
        foreach (var (method, suffix, body) in Routes())
        {
            using var request = Request(method, Url(owner.SeriesId) + suffix, body, ifMatch: "\"sa-1\"");
            using var response = await client.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);

            response.StatusCode.ShouldBe(
                HttpStatusCode.NotFound,
                $"{method} {suffix} on a foreign series must be 404: {text}");
            (await CodeAsync(response)).ShouldBe(
                "not_found",
                $"{method} {suffix} must answer not_found (the flag is on, so feature_disabled would be a bug)");
        }

        // The same GET as the owner is the positive control: the route resolves and answers something other than the
        // tenancy 404 (200 when the series has stored settings, 400 autopilot_settings_invalid when it has none), so
        // the 404s above are about the tenant filter and not about the route being absent from this host.
        using var ownClient = factory.ClientFor(owner.UserId);
        using var own = await ownClient.SendAsync(Request(HttpMethod.Get.Method, Url(owner.SeriesId), null), Ct);
        var ownText = await own.Content.ReadAsStringAsync(Ct);
        own.StatusCode.ShouldNotBe(
            HttpStatusCode.NotFound,
            $"the owner's own series must reach the handler: {ownText}");
    }

    [Fact]
    public async Task ListAutopilotRuns_LimitAndEmptyPage()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(world.UserId);

        // Plan §5: `limit ≤ 100`. 101 and 0 are refused by the handler as 400 validation_failed; the route binds the
        // value and never clamps it, so the client cannot silently get a different page than it asked for.
        foreach (var limit in new[] { 0, 101 })
        {
            using var response = await client.SendAsync(
                Request(HttpMethod.Get.Method, $"{Url(world.SeriesId)}/runs?limit={limit}", null),
                Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"limit={limit} must be refused: {text}");
            (await CodeAsync(response)).ShouldBe("validation_failed");
        }

        using var empty = await client.SendAsync(
            Request(HttpMethod.Get.Method, $"{Url(world.SeriesId)}/runs", null),
            Ct);
        using var document = JsonDocument.Parse(await empty.Content.ReadAsStringAsync(Ct));

        empty.StatusCode.ShouldBe(HttpStatusCode.OK);
        document.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0, "a fresh series has no runs");
        document.RootElement.GetProperty("nextCursor").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "the last page carries a null cursor, never an empty string");
    }

    [Fact]
    public async Task GetAutopilot_NextSlots_ReturnThreeFutureSlots()
    {
        var world = await SeedWithSettingsAsync([DayOfWeek.Monday, DayOfWeek.Thursday]);
        await SetFlagAsync(true);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.SendAsync(Request(HttpMethod.Get.Method, Url(world.SeriesId), null), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        root.GetProperty("settings").ValueKind.ShouldNotBe(
            JsonValueKind.Null,
            "the seeded series carries a settings document, so the read has one to project (W2a: the member is null only when the row has none)");

        var nextSlots = root.GetProperty("nextSlots");
        nextSlots.GetArrayLength().ShouldBe(3, "the read previews the next three slots (task §DTOs)");

        // The cadence is Mon/Thu at 19:00 Asia/Ho_Chi_Minh, so every previewed slot must land on one of those local
        // weekdays at exactly that wall-clock time — the planner's own slot function, not a fabrication of the read.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        var slots = new List<DateTimeOffset>();

        foreach (var slot in nextSlots.EnumerateArray())
        {
            var slotAt = slot.GetProperty("slotAt").GetDateTimeOffset();
            slots.Add(slotAt);

            slotAt.ShouldBeGreaterThan(Start, $"a previewed slot is in the future, not {slotAt:O}");
            slot.GetProperty("startAt").GetDateTimeOffset().ShouldBeLessThanOrEqualTo(
                slotAt,
                "plan §3.4: a run starts before the instant it publishes");

            var local = TimeZoneInfo.ConvertTime(slotAt, zone);
            local.Hour.ShouldBe(19, $"slot {local:O} is off the 19:00 cadence");
            local.Minute.ShouldBe(0, $"slot {local:O} is off the 19:00 cadence");
            (local.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Thursday).ShouldBeTrue(
                $"slot {local:O} lands on {local.DayOfWeek}, which is not in the seeded cadence");
        }

        slots.ShouldBe(slots.OrderBy(slot => slot).ToList(), "the preview is ascending, so the UI can list it as is");
        slots.Distinct().Count().ShouldBe(3, "three distinct slots, not the same one three times");

        // The clock is pinned to a Thursday 16:00 local, so the first slot is at most a week out and the three span the
        // cadence's own reach — a scheduler that answered "today every hour" would fail this bound.
        (slots[0] - Start).TotalDays.ShouldBeLessThanOrEqualTo(7.0, "the first slot is inside the cadence horizon");
        (slots[2] - slots[0]).TotalDays.ShouldBeLessThanOrEqualTo(7.0, "three Mon/Thu slots span at most seven days");
    }

    /// <summary>
    /// The <c>settings</c> the read answers is a document the write accepts: the plan §3.1 keys travel
    /// <c>snake_case</c> in both directions, so a client can put back what it just read (02.8.3 plan §3.1/§4 UC1+UC2).
    /// </summary>
    /// <remarks>
    /// The write hands the <c>settings</c> object to <see cref="AutopilotSettingsValidator"/> verbatim, and that
    /// validator reads the stored spellings and rejects every other key. A camelCase read projection would answer
    /// <c>dailyCap</c>/<c>cadence.daysOfWeek</c> where the writer demands <c>daily_cap</c>/<c>days_of_week</c>, so the
    /// round trip would answer 400 <c>autopilot_settings_invalid</c> on a document nobody edited — the failure this
    /// test pins. <c>schema_version</c> is the one §3.1 key the read omits on purpose: the write route supplies it.
    /// </remarks>
    [Fact]
    public async Task PutAutopilot_SettingsFromTheRead_RoundTripsVerbatim()
    {
        var world = await SeedSettingsOnlyAsync([DayOfWeek.Monday, DayOfWeek.Thursday]);
        await SetFlagAsync(true);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(world.UserId);

        using var read = await client.SendAsync(Request(HttpMethod.Get.Method, Url(world.SeriesId), null), Ct);
        var readText = await read.Content.ReadAsStringAsync(Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, readText);

        var tag = read.Headers.ETag;
        tag.ShouldNotBeNull("the read is where the client learns the tag the write must carry (09 README D15)");
        var tagText = tag!.ToString();

        using var document = JsonDocument.Parse(readText);
        var settings = document.RootElement.GetProperty("settings");

        settings.ValueKind.ShouldBe(JsonValueKind.Object, "the seeded series carries a settings document");

        settings.EnumerateObject().Select(property => property.Name).ShouldBe(
            [
                "cadence",
                "daily_cap",
                "buffer_days",
                "privacy",
                "veto_window_hours",
                "pre_audit_mode",
                "pre_audit_pause_after",
                "gate_thresholds_ref",
                "tone",
                "banned_themes",
                "render_lead_minutes",
                "story_lead_minutes",
            ],
            ignoreOrder: true,
            customMessage: "the settings travel the plan §3.1 keys the write route validates (`schema_version` excepted)");

        settings.GetProperty("cadence").EnumerateObject().Select(property => property.Name).ShouldBe(
            ["days_of_week", "publish_time", "time_zone"],
            ignoreOrder: true,
            customMessage: "the cadence's keys are §3.1's too, so the editor's chip list can be written back");

        // Byte-for-byte the read's own settings, with no re-spelling in between: this is the client's PUT body.
        var body = $"{{\"enabled\":false,\"settings\":{settings.GetRawText()}}}";
        using var write = await client.SendAsync(
            Request(HttpMethod.Put.Method, Url(world.SeriesId), body, ifMatch: tagText),
            Ct);
        var writeText = await write.Content.ReadAsStringAsync(Ct);

        write.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"the document the read answered must be writable unchanged: {writeText}");

        // A document nobody edited is not an edit: the read answers the same settings afterwards.
        using var reread = await client.SendAsync(Request(HttpMethod.Get.Method, Url(world.SeriesId), null), Ct);
        var rereadText = await reread.Content.ReadAsStringAsync(Ct);
        reread.StatusCode.ShouldBe(HttpStatusCode.OK, rereadText);

        using var rereadDocument = JsonDocument.Parse(rereadText);
        rereadDocument.RootElement.GetProperty("settings").GetRawText().ShouldBe(
            settings.GetRawText(),
            "writing back the read's settings must not change the stored document");
    }

    /// <summary>
    /// <c>GET …/autopilot/runs</c> walks the log in <c>slot_at DESC, id DESC</c> order, page by page, without a gap or
    /// a repeat.
    /// </summary>
    /// <remarks>
    /// <b>Deviation from the task row's name</b> (recorded in the delivery note): the row is
    /// <c>ListAutopilotRuns_CursorPagination_IsStableOnEqualSlotAt</c>, but <c>ux_autopilot_runs_series_slot</c> is a
    /// <em>unique</em> index on <c>(series_id, slot_at)</c> — the planner's arbiter (plan §3.3) — so two runs of one
    /// series can never share a <c>slot_at</c>, and the tie-break on <c>id</c> is unreachable through the API. What can
    /// be proven is what the cursor exists for: a page boundary lands exactly where the previous page stopped, in
    /// descending order, with no duplicate and no missing row. The unique index is asserted below as the evidence that
    /// the equal-<c>slot_at</c> case is impossible rather than untested.
    /// </remarks>
    [Fact]
    public async Task ListAutopilotRuns_CursorPagination_IsStableOnEqualSlotAt()
    {
        var world = await SeedWithSettingsAsync([DayOfWeek.Monday]);
        await SetFlagAsync(true);
        await SeedRunsAsync(world, count: 5);

        // Evidence for the deviation: the equal-`slot_at` case the row names cannot occur for one series.
        SqlProbe.ScalarText(
                Database,
                "SELECT coalesce(indexdef, '') FROM pg_indexes "
                + "WHERE tablename = 'autopilot_runs' AND indexdef ILIKE '%unique%' AND indexdef ILIKE '%slot_at%'")
            .ShouldContain("UNIQUE", Case.Sensitive);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(world.UserId);

        var seen = new List<Guid>();
        var slotAts = new List<DateTimeOffset>();
        string? cursor = null;

        for (var page = 1; page <= 3; page++)
        {
            var url = $"{Url(world.SeriesId)}/runs?limit=2";
            if (cursor is not null)
            {
                url += "&cursor=" + Uri.EscapeDataString(cursor);
            }

            using var response = await client.SendAsync(Request(HttpMethod.Get.Method, url, null), Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"page {page}: {text}");

            using var document = JsonDocument.Parse(text);
            var items = document.RootElement.GetProperty("items");

            foreach (var item in items.EnumerateArray())
            {
                seen.Add(item.GetProperty("id").GetGuid());
                slotAts.Add(item.GetProperty("slotAt").GetDateTimeOffset());
            }

            var next = document.RootElement.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();

            if (page < 3)
            {
                items.GetArrayLength().ShouldBe(2, $"page {page} is full and must offer a continuation");
                cursor.ShouldNotBeNullOrEmpty($"page {page} is not the last one");
            }
            else
            {
                items.GetArrayLength().ShouldBe(1, "the last page carries the fifth run");
                cursor.ShouldBeNull("the last page carries a null cursor");
            }
        }

        seen.Count.ShouldBe(5, "every seeded run is listed exactly once");
        seen.Distinct().Count().ShouldBe(5, "a cursor must not repeat the row it stopped on");
        slotAts.ShouldBe(
            slotAts.OrderByDescending(slot => slot).ToList(),
            "the log is `slot_at DESC` across page boundaries, not only inside a page");
    }

    private static string Url(Guid seriesId) =>
        $"{Route}/{seriesId.ToString("D", CultureInfo.InvariantCulture)}/autopilot";

    private static HttpRequestMessage Request(string method, string url, string? body, string? ifMatch = null)
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

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        // Read as text, never as a stream: a stream read is single-use and a test reads the problem twice.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.GetProperty("code").GetString()!;
    }

    private async Task<World> SeedAsync()
    {
        var user = await SeedUserAsync();
        var actor = new TestCurrentUser { Id = user };
        var series = await new SeriesBuilder(Database, actor, new FakeClock(Start)).CreateAsync(user, ct: Ct);

        return new World(user, series.Id);
    }

    /// <summary>
    /// Seeds an owner and one <em>fully authorized</em> autopilot series on the given cadence: a valid §3.1 settings
    /// document, an active <c>channel_connections</c> row, an accepted <c>series_autopilot_authorizations</c> row and the
    /// series' two pointers (<c>default_channel_id</c>, <c>current_autopilot_authorization_id</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The world is the one the scheduler plans from (<c>AutopilotTickHarness.CreateSeriesAsync</c>), copied here as a
    /// private helper rather than called: W2a is editing that harness file in this wave, so this file only reads it. The
    /// settings document comes from the harness's static fixtures, which are safe to call.
    /// </para>
    /// <para>
    /// The authorization's <c>text_hash</c> is the harness's <c>'a' * 64</c> placeholder on purpose: a GET re-renders the
    /// text and never compares a stored hash, so the placeholder is invisible here — and an accept/enable test (which
    /// does compare) must not reuse this helper without rendering the real hash.
    /// </para>
    /// </remarks>
    /// <param name="daysOfWeek">The cadence days.</param>
    /// <returns>The seeded owner and series.</returns>
    private async Task<World> SeedWithSettingsAsync(IReadOnlyList<DayOfWeek> daysOfWeek)
    {
        var user = await SeedUserAsync();
        var actor = new TestCurrentUser { Id = user };
        var channel = await new ChannelConnectionBuilder(Database, actor, new FakeClock(Start))
            .CreateActiveAsync(user, ct: Ct);
        var settings = AutopilotTickHarness.Settings(daysOfWeek: daysOfWeek);
        var series = await new SeriesBuilder(Database, actor, new FakeClock(Start)).CreateAsync(
            user,
            autopilotEnabled: true,
            autopilotSettings: AutopilotTickHarness.SettingsJson(settings),
            ct: Ct);

        var authorizationId = Guid.CreateVersion7();
        var stamp = Start.ToString("O", CultureInfo.InvariantCulture);

        await Database.ExecuteAsync(
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, text_hash, "
            + "topic, channel_connection_id, cadence, privacy_status, accepted_at, created_at, updated_at) "
            + $"VALUES ('{authorizationId}', '{user}', '{series.Id}', 1, 'ai.v1', '{new string('a', 64)}', 'ghost', "
            + $"'{channel.Id}', 'weekly', 'public', '{stamp}', '{stamp}', '{stamp}')",
            Ct);

        await Database.ExecuteAsync(
            $"UPDATE series SET default_channel_id = '{channel.Id}', "
            + $"current_autopilot_authorization_id = '{authorizationId}' WHERE id = '{series.Id}'",
            Ct);

        return new World(user, series.Id);
    }

    /// <summary>
    /// Seeds an owner and one series with a stored §3.1 settings document and the switch <em>off</em>: no channel, no
    /// authorization, nothing else. That is the state a first visit to the autopilot tab leaves behind, and the one the
    /// settings round trip is about — a document edit that never touches a consent.
    /// </summary>
    /// <param name="daysOfWeek">The cadence days.</param>
    /// <returns>The seeded owner and series.</returns>
    private async Task<World> SeedSettingsOnlyAsync(IReadOnlyList<DayOfWeek> daysOfWeek)
    {
        var user = await SeedUserAsync();
        var actor = new TestCurrentUser { Id = user };
        var settings = AutopilotTickHarness.Settings(daysOfWeek: daysOfWeek);
        var series = await new SeriesBuilder(Database, actor, new FakeClock(Start)).CreateAsync(
            user,
            autopilotEnabled: false,
            autopilotSettings: AutopilotTickHarness.SettingsJson(settings),
            ct: Ct);

        return new World(user, series.Id);
    }

    /// <summary>
    /// Seeds <paramref name="count"/> planned runs of the series through the TestKit's <see cref="AutopilotRunBuilder"/>
    /// (the seeder <c>AutopilotTickHarness.CreateRunAsync</c> also wraps), which writes every required column.
    /// </summary>
    /// <remarks>
    /// The instants are one local day apart and strictly descending: the unique <c>(series_id, slot_at)</c> index forbids
    /// two runs of one series on the same slot, which is why the cursor's equal-<c>slot_at</c> tie-break is unreachable
    /// through the API (the deviation the pagination test records).
    /// </remarks>
    /// <param name="world">The seeded owner and series.</param>
    /// <param name="count">How many runs to insert.</param>
    private async Task SeedRunsAsync(World world, int count)
    {
        var builder = new AutopilotRunBuilder(
            Database,
            new TestCurrentUser { Id = world.UserId },
            new FakeClock(Start));

        for (var index = 0; index < count; index++)
        {
            var slotAt = Start.AddDays(-1 - index);

            await builder.CreateAsync(
                world.UserId,
                world.SeriesId,
                slotAt: slotAt,
                startAt: slotAt.AddHours(-6),
                now: Start,
                ct: Ct);
        }
    }

    private async Task<Guid> SeedUserAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);

        return user.Id;
    }

    /// <summary>Writes (or removes) the platform-wide flag row before the host's flag cache is built.</summary>
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

    /// <summary>One seeded owner and the series it owns.</summary>
    private sealed record World(Guid UserId, Guid SeriesId);
}
