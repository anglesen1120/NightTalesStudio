using System.Net;
using Microsoft.AspNetCore.Http;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common.Errors;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// The ticket exchange of 09.8.1-T2 end to end (09 README BD11/BD14, 09.8.1 plan §5/§9): the issue
/// route, the hash-at-rest row, the single-use claim of the production
/// <c>NpgsqlHubTicketValidator</c>, the per-user rate limit, non-POST verbs, and the log-redaction
/// rule of D17.
/// </summary>
/// <remarks>
/// The host is the production Api composition root with a real PostgreSQL: the issue route goes through
/// the D4 pipeline, and every claim is the real <c>UPDATE … used_at IS NULL AND expires_at &gt; now</c>
/// against the real table. Expiry is driven by <see cref="FakeClock"/> (the host's <c>IClock</c>),
/// never by a sleep; the rate limit is the framework's own fixed window, so the 429 comes from issuing
/// the eleventh request in the same minute.
/// </remarks>
/// <param name="database">This class's isolated, migrated database.</param>
public sealed class HubTicketTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ticket_Is_Returned_Once_And_Stored_Hashed_Only()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        issued.Ticket.Length.ShouldBe(HubTicketSecret.Length, "256 random bits are 43 base64url characters (BD16)");
        issued.Ticket.ShouldMatch("^[A-Za-z0-9_-]+$");
        issued.ExpiresAt.ShouldBe(
            clock.UtcNow + HubTicketTestFlow.ShippedTtl,
            "the expiry is the row's created_at plus Realtime:TicketTtlSeconds (60, BD11)");

        var hash = HubTicketSecret.HashOf(issued.Ticket);
        hash.Length.ShouldBe(HubTicketSecret.HashLength);

        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE user_id = '{user.Id}'")
            .ShouldBe(1, "one row per issued ticket");
        SqlProbe.ScalarText(
                Database,
                "SELECT ticket_hash || '|' || user_id::text || '|' || (used_at IS NULL)::text "
                + "|| '|' || (extract(epoch FROM (expires_at - created_at)))::bigint::text FROM hub_tickets "
                + $"WHERE user_id = '{user.Id}'")
            .ShouldBe($"{hash}|{user.Id}|true|{(long)HubTicketTestFlow.ShippedTtl.TotalSeconds}");

        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{issued.Ticket}'")
            .ShouldBe(0, "the plaintext ticket is never stored (BD16)");
        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{hash}'").ShouldBe(1);
    }

    [Fact]
    public async Task Ticket_Expires_After_Ttl()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);
        issued.ExpiresAt.ShouldBe(clock.UtcNow + HubTicketTestFlow.ShippedTtl);

        clock.Advance(HubTicketTestFlow.ShippedTtl + TimeSpan.FromSeconds(1));

        var claim = await HubTicketTestFlow.ClaimAsync(factory, issued.Ticket, Ct);

        claim.IsError.ShouldBeTrue("a ticket one second past its TTL is dead");
        claim.FirstError.Code.ShouldBe(ErrorCodes.HubTicketInvalid);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}' "
                + "AND used_at IS NOT NULL")
            .ShouldBe(0, "an expired claim matches no row, so nothing was consumed");
    }

    [Fact]
    public async Task Ticket_Is_Single_Use()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        var first = await HubTicketTestFlow.ClaimAsync(factory, issued.Ticket, Ct);
        first.IsError.ShouldBeFalse();
        first.Value.UserId.ShouldBe(user.Id);

        var second = await HubTicketTestFlow.ClaimAsync(factory, issued.Ticket, Ct);
        second.IsError.ShouldBeTrue("one ticket, one connect (BD11/WF10)");
        second.FirstError.Code.ShouldBe(ErrorCodes.HubTicketInvalid);

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}' "
                + "AND used_at IS NOT NULL")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Two_Concurrent_Claims_One_Wins()
    {
        var clock = new FakeClock();

        // Two Api instances over one database — the shape a load balancer produces (BD11): the ticket is
        // issued by the first and the two claims run against different hosts.
        using var issuer = new ApiFactory(Database, clock);
        using var other = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);

        using var client = issuer.ClientFor(user.Id);
        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        var claims = await Task.WhenAll(
            HubTicketTestFlow.ClaimAsync(issuer, issued.Ticket, Ct),
            HubTicketTestFlow.ClaimAsync(other, issued.Ticket, Ct));

        claims.Count(result => !result.IsError).ShouldBe(1, "exactly one claim wins the row");
        claims.Count(result => result.IsError).ShouldBe(1);
        claims.Single(result => !result.IsError).Value.UserId.ShouldBe(user.Id);

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}' "
                + "AND used_at IS NOT NULL")
            .ShouldBe(1, "the single UPDATE is the whole claim: one non-null used_at");
    }

    [Fact]
    public async Task Expired_Ticket_Is_Rejected()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);
        clock.Advance(HubTicketTestFlow.ShippedTtl + TimeSpan.FromMinutes(5));

        var expired = await HubTicketTestFlow.ClaimAsync(factory, issued.Ticket, Ct);
        var unknown = await HubTicketTestFlow.ClaimAsync(factory, "never-issued-ticket", Ct);

        expired.IsError.ShouldBeTrue();
        expired.FirstError.Code.ShouldBe(ErrorCodes.HubTicketInvalid);
        expired.FirstError.Code.ShouldBe(
            unknown.FirstError.Code,
            "an expired ticket is indistinguishable from one that never existed");

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}' "
                + "AND used_at IS NOT NULL")
            .ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_Or_Tampered_Ticket_Is_Rejected()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        // One flipped character of the base64url value: a different hash, so the primary key misses.
        var tampered = (issued.Ticket[0] == 'A' ? "B" : "A") + issued.Ticket[1..];

        using (var missing = await client.PostAsync(HubTicketTestFlow.NegotiatePath, content: null, Ct))
        {
            await HubTicketTestFlow.AssertHubTicketInvalidAsync(missing, Ct);
        }

        using (var refused = await client.PostAsync(
                   HubTicketTestFlow.NegotiatePath + "?ticket=" + Uri.EscapeDataString(tampered),
                   content: null,
                   Ct))
        {
            await HubTicketTestFlow.AssertHubTicketInvalidAsync(refused, Ct);
        }

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}' "
                + "AND used_at IS NOT NULL")
            .ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}'")
            .ShouldBe(1, "the real row is untouched");
    }

    [Fact]
    public async Task Missing_Ticket_Is_Rejected_With_401_HubTicketInvalid()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        using var client = factory.AnonymousClient();

        // No ticket at all: the HubTicket scheme does not even attempt authentication, and the hub's
        // [Authorize] turns that into the documented challenge.
        using var response = await client.PostAsync(HubTicketTestFlow.NegotiatePath, content: null, Ct);

        await HubTicketTestFlow.AssertHubTicketInvalidAsync(response, Ct);
    }

    [Fact]
    public async Task Ticket_Requires_Access_Token()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        using var client = factory.AnonymousClient();

        var before = SqlProbe.Count(Database, "SELECT count(*) FROM hub_tickets");

        using var response = await client.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the route is [Authorize]d (BD11)");
        SqlProbe.Count(Database, "SELECT count(*) FROM hub_tickets")
            .ShouldBe(before, "an anonymous caller mints nothing");
    }

    [Fact]
    public async Task Eleventh_Ticket_In_A_Minute_Is_429_RateLimited_With_RetryAfter()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        for (var ticket = 1; ticket <= 10; ticket++)
        {
            using var accepted = await client.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK, $"ticket {ticket} is inside the ten-per-minute window");
        }

        using var rejected = await client.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the eleventh ticket of one user is refused (BD14)");
        rejected.Headers.RetryAfter.ShouldNotBeNull("a 429 advertises when to come back (09 README D7)");

        var problem = await HubTicketTestFlow.ProblemAsync(rejected, Ct);
        problem.Code.ShouldBe(ErrorCodes.RateLimited);
        problem.Status.ShouldBe(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task Rate_Limit_Is_Per_User()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var first = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        var second = await HubTicketTestFlow.CreateUserAsync(Database, Ct);

        using var firstClient = factory.ClientFor(first.Id);
        using var secondClient = factory.ClientFor(second.Id);

        for (var ticket = 1; ticket <= 10; ticket++)
        {
            using var accepted = await firstClient.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var over = await firstClient.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct))
        {
            over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        }

        // A different sub has a bucket of its own: the policy partitions on the user (BD14).
        using var other = await secondClient.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.OK, "another user is inside their own window");
    }

    [Fact]
    public async Task Claim_Then_Connect_Succeeds_End_To_End()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        await using (var socket = await SignalRTestClient.ConnectAsync(factory.Server, issued.Ticket, null, Ct))
        {
            socket.ShouldNotBeNull("the ticket authenticates the hub handshake end to end");
        }

        // The connect consumed the ticket: replaying it answers the same 401 a stranger gets.
        using var replay = await client.PostAsync(
            HubTicketTestFlow.NegotiatePath + "?ticket=" + Uri.EscapeDataString(issued.Ticket),
            content: null,
            Ct);

        await HubTicketTestFlow.AssertHubTicketInvalidAsync(replay, Ct);
    }

    [Fact]
    public async Task Endpoint_Rejects_Non_Post_Verbs()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        using (var get = await client.GetAsync(HubTicketTestFlow.TicketPath, Ct))
        {
            get.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed, "only POST is mapped on the ticket route");
        }

        using (var put = await client.PutAsync(HubTicketTestFlow.TicketPath, content: null, Ct))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        }
    }

    [Fact]
    public async Task No_Log_Line_Contains_The_Ticket()
    {
        var clock = new FakeClock();
        var logs = new ProblemExchange.CapturingLogs();

        using var factory = new ApiFactory(Database, clock).WithLoggerProvider(logs);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);
        var hash = HubTicketSecret.HashOf(issued.Ticket);

        logs.Entries.ShouldNotBeEmpty("the host must have logged something for this scan to mean anything");
        logs.Entries
            .Select(entry => entry.Message)
            .ShouldNotContain(
                line => line.Contains(issued.Ticket, StringComparison.Ordinal),
                "the ticket is a bearer secret and must never reach a log line (BD16/D17)");
        logs.Entries
            .Select(entry => entry.Message)
            .ShouldNotContain(
                line => line.Contains(hash, StringComparison.Ordinal),
                "nor must the hash a row stores");
    }
}
