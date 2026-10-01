using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Api.Realtime;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Realtime.Jobs;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Jobs;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// QA edge coverage of 09.8.1-T2 on top of <c>HubTicketTests</c> and
/// <c>HubTicketClaimConcurrencyTests</c> (09 README BD11/BD14/BD16): the exact expiry boundary,
/// replay on a second socket, deleted and suspended owners, the caller-versus-owner split of a
/// claimed socket, an HTTP-level claim race, refusal shapes, <c>no-store</c>, the opportunistic
/// per-user sweep, the cleanup batch order and the Worker's resolution of the job.
/// </summary>
/// <remarks>
/// Everything runs through the production composition root against the class's real PostgreSQL
/// (D13); time is the host's <see cref="FakeClock"/>, never a sleep. The harness is the same one
/// <c>HubTicketTestFlow</c> documents, so a row asserted here is a row the pipeline committed.
/// </remarks>
/// <param name="database">This class's isolated, migrated database.</param>
public sealed class HubTicketQaEdgeTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Replayed_Ticket_On_A_Second_Connect_Is_Refused()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        await using (var socket = await SignalRTestClient.ConnectAsync(factory.Server, issued.Ticket, null, Ct))
        {
            socket.ShouldNotBeNull();
        }

        // The ticket died with the first connect: a second socket with the same value is refused
        // before the upgrade (TestServer surfaces the 401 as an InvalidOperationException).
        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => SignalRTestClient.ConnectAsync(factory.Server, issued.Ticket, null, Ct));
        refused.Message.ShouldContain("401"); // the upgrade was refused, not negotiated
        refused.Message.ShouldNotContain(
            issued.Ticket,
            Case.Sensitive,
            "the refusal must not echo the bearer secret back");

        using var negotiate = await client.PostAsync(
            HubTicketTestFlow.NegotiatePath + "?ticket=" + Uri.EscapeDataString(issued.Ticket),
            content: null,
            Ct);

        await HubTicketTestFlow.AssertHubTicketInvalidAsync(negotiate, Ct);
    }

    [Fact]
    public async Task Ticket_Expiry_Boundary_Is_Strictly_Greater_Than()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var expiring = await HubTicketTestFlow.IssueAsync(client, Ct);
        var stillLive = await HubTicketTestFlow.IssueAsync(client, Ct);

        // One second inside the TTL the ticket still claims.
        clock.Advance(HubTicketTestFlow.ShippedTtl - TimeSpan.FromSeconds(1));
        var inside = await HubTicketTestFlow.ClaimAsync(factory, stillLive.Ticket, Ct);
        inside.IsError.ShouldBeFalse("expires_at > now() holds one second before the deadline");

        // Exactly at expires_at the predicate is false (strictly greater than) — the boundary is dead.
        clock.Advance(TimeSpan.FromSeconds(1));
        var atBoundary = await HubTicketTestFlow.ClaimAsync(factory, expiring.Ticket, Ct);

        atBoundary.IsError.ShouldBeTrue("a claim at expires_at matches no row");
        atBoundary.FirstError.Code.ShouldBe(ErrorCodes.HubTicketInvalid);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(expiring.Ticket)}' "
                + "AND used_at IS NOT NULL")
            .ShouldBe(0, "a refused claim consumes nothing");
    }

    [Fact]
    public async Task Ticket_Of_A_Deleted_User_Cannot_Be_Claimed()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);
        var hash = HubTicketSecret.HashOf(issued.Ticket);

        SqlProbe.Execute(Database, $"DELETE FROM users WHERE id = '{user.Id}'");
        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{hash}'")
            .ShouldBe(0, "ON DELETE CASCADE removed the ticket with its owner (BD11)");

        var claim = await HubTicketTestFlow.ClaimAsync(factory, issued.Ticket, Ct);

        claim.IsError.ShouldBeTrue();
        claim.FirstError.Code.ShouldBe(ErrorCodes.HubTicketInvalid);

        // The wire shape is the same refusal a stranger gets for a ticket that never existed.
        using var negotiate = await client.PostAsync(
            HubTicketTestFlow.NegotiatePath + "?ticket=" + Uri.EscapeDataString(issued.Ticket),
            content: null,
            Ct);
        await HubTicketTestFlow.AssertHubTicketInvalidAsync(negotiate, Ct);
    }

    [Fact]
    public async Task Ticket_Of_A_Suspended_Or_Stamp_Rotated_User_Still_Claims()
    {
        // [RULED — README §B10 WT37]: the claim reads only the owner's live role — no status and no
        // security_stamp column exists on hub_tickets (plan open item 1). This test pins the ruled
        // behaviour: revocation of such a user reaches a live socket only through the `security`
        // NOTIFY of 09.8.1-T1, never through the claim.
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var suspended = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        var rotated = await HubTicketTestFlow.CreateUserAsync(Database, Ct);

        IssuedTicket suspendedTicket;
        using (var client = factory.ClientFor(suspended.Id))
        {
            suspendedTicket = await HubTicketTestFlow.IssueAsync(client, Ct);
        }

        IssuedTicket rotatedTicket;
        using (var client = factory.ClientFor(rotated.Id))
        {
            rotatedTicket = await HubTicketTestFlow.IssueAsync(client, Ct);
        }

        SqlProbe.Execute(Database, $"UPDATE users SET status = 'suspended' WHERE id = '{suspended.Id}'");
        SqlProbe.Execute(
            Database,
            $"UPDATE users SET security_stamp = '{Guid.NewGuid():N}' WHERE id = '{rotated.Id}'");

        var suspendedClaim = await HubTicketTestFlow.ClaimAsync(factory, suspendedTicket.Ticket, Ct);
        var rotatedClaim = await HubTicketTestFlow.ClaimAsync(factory, rotatedTicket.Ticket, Ct);

        suspendedClaim.IsError.ShouldBeFalse("WT37: status is not read at claim time");
        suspendedClaim.Value.UserId.ShouldBe(suspended.Id);
        rotatedClaim.IsError.ShouldBeFalse("WT37: the stamp is not compared at claim time");
        rotatedClaim.Value.UserId.ShouldBe(rotated.Id);
    }

    [Fact]
    public async Task A_Connect_Authenticates_As_The_Tickets_Owner_Not_The_Caller()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var owner = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        var other = await HubTicketTestFlow.CreateUserAsync(Database, Ct);

        using var client = factory.ClientFor(owner.Id);
        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        await using var socket = await SignalRTestClient.ConnectAsync(factory.Server, issued.Ticket, null, Ct);

        var registry = factory.Services.GetRequiredService<RealtimeConnectionRegistry>();
        registry.Count.ShouldBe(1);

        // The registry is what the `security` NOTIFY aborts by: a revocation sweep for a different
        // user must not touch this socket, and the owner's sweep is what closes it — proof the
        // claimed identity is the ticket's owner and nothing the caller presented.
        registry.AbortUser(other.Id).ShouldBe(0, "the socket belongs to the ticket's owner");
        registry.Count.ShouldBe(1);

        using var bound = RealtimeHarness.Bounded(Ct);
        registry.AbortUser(owner.Id).ShouldBe(1);
        (await socket.ClosedAsync(bound.Token)).ShouldBeTrue("the owner's abort closes the socket");
    }

    [Fact]
    public async Task Two_Http_Claims_Of_One_Ticket_Exactly_One_Wins()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        var path = HubTicketTestFlow.NegotiatePath + "?ticket=" + Uri.EscapeDataString(issued.Ticket);
        var responses = await Task.WhenAll(
            client.PostAsync(path, content: null, Ct),
            client.PostAsync(path, content: null, Ct));

        var ok = 0;
        var refused = 0;
        foreach (var response in responses)
        {
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    ok++;
                }
                else
                {
                    await HubTicketTestFlow.AssertHubTicketInvalidAsync(response, Ct);
                    refused++;
                }
            }
        }

        ok.ShouldBe(1, "the atomic UPDATE hands the ticket to exactly one claimant (WF10)");
        refused.ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}' "
                + "AND used_at IS NOT NULL")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Anonymous_Ticket_Request_Is_401_And_Writes_No_Row()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        using var client = factory.AnonymousClient();

        var before = SqlProbe.Count(Database, "SELECT count(*) FROM hub_tickets");

        using var response = await client.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        SqlProbe.Count(Database, "SELECT count(*) FROM hub_tickets")
            .ShouldBe(before, "an anonymous caller mints nothing");
    }

    [Fact]
    public async Task A_Rate_Limited_Ticket_Request_Mints_No_Row()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        for (var ticket = 1; ticket <= 10; ticket++)
        {
            using var accepted = await client.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var before = SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM hub_tickets WHERE user_id = '{user.Id}'");

        using var rejected = await client.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the `hub` policy is 10/min per user (BD14)");
        rejected.Headers.RetryAfter.ShouldNotBeNull();

        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE user_id = '{user.Id}'")
            .ShouldBe(before, "a 429 never reaches the handler, so no row is written");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE user_id = '{user.Id}' AND used_at IS NOT NULL")
            .ShouldBe(0, "and nothing was claimed either");
    }

    [Fact]
    public async Task Malformed_Presented_Values_Are_The_Same_401()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        // A valid ticket sits in the table; none of the hostile values below may touch it.
        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        var presented = new[]
        {
            "with space inside",
            new string('x', 4096),
            "日本語のチケット",
            "dGVybWludGxl%3D%3D", // percent-encoded '=' — a value, never a decoded second parameter
        };

        foreach (var value in presented)
        {
            using var response = await client.PostAsync(
                HubTicketTestFlow.NegotiatePath + "?ticket=" + Uri.EscapeDataString(value),
                content: null,
                Ct);

            await HubTicketTestFlow.AssertHubTicketInvalidAsync(response, Ct);
        }

        // A token-shaped value under another parameter name is never picked up: the scheme reads
        // `ticket` only, so the access-token-in-URL shape the design forbids cannot authenticate.
        using (var wrongParameter = await client.PostAsync(
                   HubTicketTestFlow.NegotiatePath + "?access_token=" + Uri.EscapeDataString(issued.Ticket),
                   content: null,
                   Ct))
        {
            await HubTicketTestFlow.AssertHubTicketInvalidAsync(wrongParameter, Ct);
        }

        // An empty `ticket=` is no attempt at all — the scheme challenges with the same 401 shape.
        using (var empty = await client.PostAsync(HubTicketTestFlow.NegotiatePath + "?ticket=", content: null, Ct))
        {
            await HubTicketTestFlow.AssertHubTicketInvalidAsync(empty, Ct);
        }

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}' "
                + "AND used_at IS NOT NULL")
            .ShouldBe(0, "no hostile presentation consumed the real ticket");
    }

    [Fact]
    public async Task Refusals_Never_Echo_Or_Log_The_Presented_Ticket()
    {
        var clock = new FakeClock();
        var logs = new ProblemExchange.CapturingLogs();
        using var factory = new ApiFactory(Database, clock).WithLoggerProvider(logs);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        // A value unique to this run, presented and refused: neither the response body nor any log
        // line may carry it (BD16/D17 — the value is a bearer secret).
        var canary = $"qa-canary-{Guid.NewGuid():N}";

        using var response = await client.PostAsync(
            HubTicketTestFlow.NegotiatePath + "?ticket=" + Uri.EscapeDataString(canary),
            content: null,
            Ct);

        await HubTicketTestFlow.AssertHubTicketInvalidAsync(response, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(canary, Case.Sensitive, "the refusal never echoes the presented value");

        // The feature's own lines never carry the presented value. The framework's access lines
        // ("Request starting/finished … ?ticket=…") still render the raw query string — a documented
        // residual of 09.8.1-T1, assigned to the 09.9.3-T2 redaction processor (plan §8); those lines
        // are filtered here so this test fails only if the feature itself leaks.
        logs.Entries.ShouldNotBeEmpty();
        var featureLines = logs.Entries
            .Where(entry => !entry.Message.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal))
            .Select(entry => entry.Message)
            .ToList();

        featureLines.ShouldContain(
            line => line.Contains("hub.ticket.rejected", StringComparison.Ordinal),
            "the scan only means something if the refusal was actually logged");
        featureLines.ShouldNotContain(
            line => line.Contains(canary, StringComparison.Ordinal),
            "a refused ticket must not reach a log line of this feature");

        // …and the residual is real but bounded: the only lines carrying the value are the
        // framework's request diagnostics the redaction processor of 09.9.3-T2 owns.
        logs.Entries
            .Where(entry => entry.Message.Contains(canary, StringComparison.Ordinal))
            .ShouldAllBe(
                entry => entry.Message.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal),
                "only framework access lines may carry it today (09.9.3-T2 residual)");
    }

    [Fact]
    public async Task The_Issued_Ticket_Response_Carries_NoStore()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        using var response = await client.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl!.NoStore.ShouldBe(
            true,
            "a ticket is a bearer secret for its 60 seconds; no cache may keep it");
    }

    [Fact]
    public async Task A_Second_Issue_Sweeps_The_Callers_Own_Expired_Rows()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        var other = await HubTicketTestFlow.CreateUserAsync(Database, Ct);

        using (var client = factory.ClientFor(user.Id))
        {
            _ = await HubTicketTestFlow.IssueAsync(client, Ct);
        }

        using (var client = factory.ClientFor(other.Id))
        {
            _ = await HubTicketTestFlow.IssueAsync(client, Ct);
        }

        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE user_id = '{user.Id}'").ShouldBe(1);

        clock.Advance(HubTicketTestFlow.ShippedTtl + TimeSpan.FromSeconds(1));

        using var second = factory.ClientFor(user.Id);
        _ = await HubTicketTestFlow.IssueAsync(second, Ct);

        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE user_id = '{user.Id}'")
            .ShouldBe(1, "the issue path deletes the caller's expired rows in the same transaction");
        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE user_id = '{other.Id}'")
            .ShouldBe(1, "another user's expired row is the hourly job's, not this request's");
    }

    [Fact]
    public async Task Cleanup_Deletes_The_Oldest_Batch_First()
    {
        var clock = new FakeClock();
        var now = clock.UtcNow;
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);

        // Three rows past the 24-hour retention, batch size two: the sweep must take the two oldest —
        // the subquery orders by expires_at — so 'qa-newest' is the row that survives this run.
        await InsertTicketAsync(user.Id, "qa-oldest", now - TimeSpan.FromHours(30));
        await InsertTicketAsync(user.Id, "qa-middle", now - TimeSpan.FromHours(28));
        await InsertTicketAsync(user.Id, "qa-newest", now - TimeSpan.FromHours(26));

        await using var host = new WorkerHost(Database, clock)
            .WithSetting(
                $"{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.TicketCleanupBatchSize)}",
                "2");
        await host.StartAsync(Ct);

        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<HubTicketCleanupJob>().RunAsync(Ct);
        }

        SqlProbe.Count(Database, "SELECT count(*) FROM hub_tickets WHERE ticket_hash = 'qa-oldest'").ShouldBe(0);
        SqlProbe.Count(Database, "SELECT count(*) FROM hub_tickets WHERE ticket_hash = 'qa-middle'").ShouldBe(0);
        SqlProbe.Count(Database, "SELECT count(*) FROM hub_tickets WHERE ticket_hash = 'qa-newest'")
            .ShouldBe(1, "one run honours the configured batch and keeps the newest of the backlog");
    }

    [Fact]
    public async Task Worker_Starts_With_The_Ticket_Sweep_Resolvable()
    {
        var clock = new FakeClock();

        await using var host = new WorkerHost(Database, clock);
        await host.StartAsync(Ct);

        // The registrar upserted it at start, and the job resolves from the host's own graph — the
        // composition IHubTicketStore needs (AddRealtime) is what this run proves is present.
        var upsert = host.Jobs.RecurringUpserts
            .Single(call => call.RecurringId == HubTicketCleanupJobRegistration.RecurringId);
        upsert.JobType.ShouldBe(nameof(HubTicketCleanupJob));

        using var scope = host.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<HubTicketCleanupJob>();
        await job.RunAsync(Ct);
    }

    [Fact]
    public async Task A_Dead_Database_Is_A_Server_Error_With_No_Row()
    {
        var clock = new FakeClock();

        // A port nothing listens on: the command fails at BeginTransactionAsync, outside the unit of
        // work's commit mapping, so the documented 503 surfaces as the exception handler's 500 — the
        // QA observation recorded in this run's matrix.
        var dead = new NpgsqlConnectionStringBuilder(Database.ConnectionString) { Port = 1, Timeout = 3 }
            .ConnectionString;
        using var factory = new ApiFactory(Database, clock)
            .WithSetting($"ConnectionStrings:{PersistenceOptions.ConnectionStringName}", dead);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await client.PostAsync(HubTicketTestFlow.TicketPath, content: null, Ct);

        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(500, "a refused command is a server error");
        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        response.Headers.ShouldNotContain(
            header => header.Value.Any(value => value.Length == HubTicketSecret.Length),
            "no 43-char ticket ever leaves a failed exchange");
    }

    /// <summary>
    /// Writes one ticket row with the timestamps a case needs — the FakeClock's value, never
    /// PostgreSQL's <c>now()</c>, because the store's cutoff is computed from it.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="hash">The row's primary key.</param>
    /// <param name="expiresAt">When the row expires.</param>
    private Task InsertTicketAsync(Guid userId, string hash, DateTimeOffset expiresAt)
    {
        var created = expiresAt - TimeSpan.FromSeconds(RealtimeOptions.DefaultTicketTtlSeconds);

        return Database.ExecuteAsync(
            $"""
             INSERT INTO hub_tickets (ticket_hash, user_id, created_at, expires_at, used_at)
             VALUES ('{hash}', '{userId}', {Timestamp(created)}, {Timestamp(expiresAt)}, null)
             """,
            Ct);
    }

    private static string Timestamp(DateTimeOffset value) =>
        $"'{value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}+00'::timestamptz";
}
