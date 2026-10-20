using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// QA pass over <c>DELETE /api/v1/publications/{id}</c> (06.2.6-T1). It pins what the implementer's
/// <c>CancelPublicationTests</c> leaves open: every later state answers 409 with no write, two concurrent deletes
/// of one row cancel and release once, an unknown id and a non-GUID segment are 404 with no side effect, the
/// release respects its boundaries (above one, at zero, awaiting consent, a past day), and a stale tag answers 412
/// with the row's current ETag.
/// </summary>
/// <remarks>
/// Each test seeds its own owner through the TestKit builders. Outbox and audit assertions are scoped to the
/// publication id the test seeded. The helpers are copies of the private helpers in <c>CancelPublicationTests</c>,
/// which stay private there.
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class CancelPublicationQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string PublicationsRoute = "/api/v1/publications";

    private const string FeatureKey = "youtube_publish";

    private const string OutboxEventType = "publication_updated.v1";

    private const string AuditAction = "publishing.cancelled";

    private const string QuotaDay = "2026-10-02";

    private const string OtherDay = "2026-10-03";

    private const string PastDay = "2026-10-01";

    private static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Theory]
    [InlineData(PublicationStatus.Processing, "processing")]
    [InlineData(PublicationStatus.Published, "published")]
    [InlineData(PublicationStatus.Failed, "failed")]
    public async Task LaterStates_Answer409_AndWriteNothing_AndKeepTheReservedUnit(
        PublicationStatus status,
        string expectedStatus)
    {
        // The upload has started or ended: the row is not deletable, so the refusal writes nothing and the
        // reserved unit stays reserved.
        var world = await SeedAsync(status, QuotaPayload(QuotaDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 1, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var before = Xmin(world.PublicationId);
        var ledgerBefore = QuotaRow(QuotaDay);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));
        var problem = await ReadProblemAsync(response, HttpStatusCode.Conflict);

        problem.Code.ShouldBe("publication_invalid_state");
        AssertUntouched(world, expectedStatus, "a refused cancel writes no event and no audit row");
        Xmin(world.PublicationId).ShouldBe(before, "a refused cancel writes nothing to the row");
        QuotaRow(QuotaDay).ShouldBe(ledgerBefore, "a refused cancel keeps the reserved unit");
    }

    [Fact]
    public async Task ConcurrentDeletes_OfOneRow_CancelOnceAndReleaseOnce()
    {
        // Two DELETEs with the same pre-cancel tag race on one scheduled row. The row lock serialises them: the
        // winner cancels and releases, and the loser sees `cancelled` and answers 204 without a second write,
        // event, audit row or release. Two units reserved make a double release visible as 0 instead of 1.
        var world = await SeedAsync(PublicationStatus.Scheduled, QuotaPayload(QuotaDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 2, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);
        var tag = CurrentTag(world.PublicationId);

        var firstCall = DeleteAsync(client, world.PublicationId, tag);
        var secondCall = DeleteAsync(client, world.PublicationId, tag);

        using var first = await firstCall;
        using var second = await secondCall;

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync(Ct));
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent, await second.Content.ReadAsStringAsync(Ct));

        PublicationStatusOf(world.PublicationId).ShouldBe("cancelled");
        OutboxPayloads(world.PublicationId).Count.ShouldBe(1, "exactly one publication_updated.v1 for two deletes");
        AuditData(world.PublicationId).Count.ShouldBe(1, "exactly one publishing.cancelled row for two deletes");
        UnitsUsed(QuotaDay).ShouldBe(1, "the unit is released exactly once, not twice");
    }

    [Fact]
    public async Task UnknownPublicationId_Is404_AndTouchesNoRow()
    {
        // An id that names no row gets the same 404 a foreign id gets. The owner's own row and the ledger stay put.
        var world = await SeedAsync(PublicationStatus.Scheduled, QuotaPayload(QuotaDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 1, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);
        var ledgerBefore = QuotaRow(QuotaDay);

        using var response = await DeleteAsync(client, Guid.CreateVersion7(), CurrentTag(world.PublicationId));
        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);

        problem.Code.ShouldBe("not_found");
        AssertUntouched(world, "scheduled", "an unknown id writes nothing on the owner's row");
        QuotaRow(QuotaDay).ShouldBe(ledgerBefore, "an unknown id releases nothing");
    }

    [Fact]
    public async Task NonGuidRouteSegment_IsNotFound_AndTouchesNoRow()
    {
        // The route constrains {id} to a GUID, so the request never reaches the handler.
        var world = await SeedAsync(PublicationStatus.Scheduled, QuotaPayload(QuotaDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 1, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{PublicationsRoute}/not-a-guid");
        request.Headers.TryAddWithoutValidation("If-Match", CurrentTag(world.PublicationId));
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        AssertUntouched(world, "scheduled", "a non-GUID id never reaches the handler");
        UnitsUsed(QuotaDay).ShouldBe(1, "a non-GUID id releases nothing");
    }

    [Fact]
    public async Task UnitsUsedAboveOne_DecrementsByExactlyOne_AndLeavesOtherDaysAlone()
    {
        var world = await SeedAsync(PublicationStatus.Scheduled, QuotaPayload(QuotaDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 3, unitsLimit: 6);
        await SeedQuotaRowAsync(OtherDay, unitsUsed: 2, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);
        var otherBefore = QuotaRow(OtherDay);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        UnitsUsed(QuotaDay).ShouldBe(2, "one reserved unit is given back, not more");
        QuotaRow(OtherDay).ShouldBe(otherBefore, "another day's ledger row is never touched");
    }

    [Fact]
    public async Task UnitsUsedAtZero_StaysAtZero_AndTheCancelStillSucceeds()
    {
        // The ledger guards units_used > 0, so the release can never drive the counter below zero.
        var world = await SeedAsync(PublicationStatus.Scheduled, QuotaPayload(QuotaDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 0, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        UnitsUsed(QuotaDay).ShouldBe(0, "the counter never goes negative");
        PublicationStatusOf(world.PublicationId).ShouldBe("cancelled");
        OutboxPayloads(world.PublicationId).Count.ShouldBe(1);
        AuditData(world.PublicationId).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AwaitingConsent_WithReservedKey_ReleasesTheUnit()
    {
        // Both cancellable states release the same way: the key decides the release, not the state.
        var world = await SeedAsync(PublicationStatus.AwaitingConsent, QuotaPayload(QuotaDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 1, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        UnitsUsed(QuotaDay).ShouldBe(0);
        PublicationStatusOf(world.PublicationId).ShouldBe("cancelled");
    }

    [Fact]
    public async Task PastDayKey_ReleasesThatDay_NotTheClockDay()
    {
        // The key names the day the unit was reserved on. The clock's own day must not be touched.
        var world = await SeedAsync(PublicationStatus.Scheduled, QuotaPayload(PastDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(PastDay, unitsUsed: 1, unitsLimit: 6);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 1, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        UnitsUsed(PastDay).ShouldBe(0, "the key's day gives its unit back");
        UnitsUsed(QuotaDay).ShouldBe(1, "the clock's day keeps its unit");
    }

    [Fact]
    public async Task StaleTag_Answers412_WithTheCurrentETag_AndWritesNothing()
    {
        // The 412 carries the row's current strong tag so the client can refetch. The refused cancel writes nothing.
        var world = await SeedAsync(PublicationStatus.Scheduled, QuotaPayload(QuotaDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 1, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var before = Xmin(world.PublicationId);
        var stale = ETags.Publication(before - 1);
        var ledgerBefore = QuotaRow(QuotaDay);

        using var response = await DeleteAsync(client, world.PublicationId, stale);
        var problem = await ReadProblemAsync(response, HttpStatusCode.PreconditionFailed);

        problem.Code.ShouldBe("publication_conflict");
        response.Headers.ETag?.Tag.ShouldBe(CurrentTag(world.PublicationId), "the 412 names the row's current tag");
        AssertUntouched(world, "scheduled", "a stale If-Match writes nothing");
        QuotaRow(QuotaDay).ShouldBe(ledgerBefore, "a stale If-Match releases nothing");
    }

    /// <summary>Seeds one owner, its episode, an active channel and one publication in <paramref name="status"/>.</summary>
    private async Task<World> SeedAsync(PublicationStatus status, string? platformPayloadJson = null)
    {
        var owner = new TestCurrentUser();
        var userId = (await new UserBuilder(Database, owner).CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = userId };
        var clock = new FakeClock(Start);

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            userId,
            title: "Chuỗi QA",
            createdAt: Start,
            ct: Ct);

        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập QA.",
            status: EpisodeStatus.Ready,
            createdAt: Start,
            ct: Ct);

        var display = $"NTS QA Channel {Guid.CreateVersion7():N}";
        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            ChannelConnectionStatus.Active,
            display,
            ct: Ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            connection.Id,
            status: status,
            platformPayloadJson: platformPayloadJson,
            createdAt: Start,
            ct: Ct);

        return new World(userId, episode.Id, publicationId);
    }

    /// <summary>Writes the one flag row these routes read.</summary>
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

    /// <summary>The host, with the clock pinned before the services are first read.</summary>
    private ApiFactory NewFactory() => new(Database, new FakeClock(Start));

    /// <summary><c>DELETE /api/v1/publications/{id}</c> with the given <c>If-Match</c>.</summary>
    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid publicationId, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{PublicationsRoute}/{publicationId}");

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Asserts the status and the problem body, and returns the body.</summary>
    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)expected);

        return problem;
    }

    /// <summary>The row was not written: its status is unchanged, and neither an event nor an audit row exists.</summary>
    private void AssertUntouched(World world, string expectedStatus, string reason)
    {
        PublicationStatusOf(world.PublicationId).ShouldBe(expectedStatus, reason);
        OutboxPayloads(world.PublicationId).ShouldBeEmpty(reason);
        AuditData(world.PublicationId).ShouldBeEmpty(reason);
    }

    /// <summary>The row's current strong ETag, read from <c>xmin</c>.</summary>
    private string CurrentTag(Guid publicationId) => ETags.Publication(Xmin(publicationId));

    /// <summary>The <c>xmin</c> of the row, as <c>ETags.Publication</c> spells it.</summary>
    private uint Xmin(Guid publicationId) =>
        uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM publications WHERE id = '{publicationId}'"),
            CultureInfo.InvariantCulture);

    private string PublicationStatusOf(Guid publicationId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM publications WHERE id = '{publicationId}'");

    /// <summary>The <c>data</c> of this command's audit rows for the publication.</summary>
    private List<string> AuditData(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{AuditAction}' "
            + $"AND entity_id = '{publicationId}'");

    /// <summary>The payloads of the event this cancel raised for the publication.</summary>
    private List<string> OutboxPayloads(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{OutboxEventType}' "
            + $"AND payload->>'publicationId' = '{publicationId}'");

    /// <summary>Seeds one <c>platform_quota_usage</c> row for the day, replacing any earlier one.</summary>
    private async Task SeedQuotaRowAsync(string day, int unitsUsed, int unitsLimit)
    {
        await Database.ExecuteAsync(
            $"DELETE FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'",
            Ct);

        await Database.ExecuteAsync(
            "INSERT INTO platform_quota_usage (platform, quota_day, units_used, units_limit, created_at, updated_at) "
            + $"VALUES ('youtube', '{day}', {unitsUsed}, {unitsLimit}, now(), now())",
            Ct);
    }

    /// <summary>The <c>units_used</c> of the day's row.</summary>
    private int UnitsUsed(string day) =>
        int.Parse(
            SqlProbe.ScalarText(
                Database,
                $"SELECT units_used FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'"),
            CultureInfo.InvariantCulture);

    /// <summary>A whole ledger row as one text, so "identical before and after" is one assertion.</summary>
    private string QuotaRow(string day) =>
        SqlProbe.ScalarText(
            Database,
            "SELECT platform || '|' || quota_day::text || '|' || units_used::text || '|' || units_limit::text "
            + $"|| '|' || updated_at::text FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'");

    /// <summary>The <c>platform_payload</c> reserved-day key (06.2.3-T2's contract): a Pacific day as ISO text.</summary>
    private static string QuotaPayload(string day) => $$"""{"quotaReservedDay":"{{day}}"}""";

    private sealed record World(Guid UserId, Guid EpisodeId, Guid PublicationId);

    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
