using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// The claim's own guarantees of 09.8.1-T2 (09 README BD11/WF10): the atomic
/// <c>UPDATE … SET used_at</c>, the owner a claim returns, and the cascade that keeps a deleted
/// account from leaving ticket rows behind.
/// </summary>
/// <remarks>
/// All three go through the production validator against a real PostgreSQL; the row-level effects are
/// asserted in SQL, because the point is what the table holds after the statement, not what the
/// handler believed.
/// </remarks>
/// <param name="database">This class's isolated, migrated database.</param>
public sealed class HubTicketClaimConcurrencyTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Claim_Sets_UsedAt_Atomically()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        using var client = factory.ClientFor(user.Id);

        var issued = await HubTicketTestFlow.IssueAsync(client, Ct);

        var claim = await HubTicketTestFlow.ClaimAsync(factory, issued.Ticket, Ct);

        claim.IsError.ShouldBeFalse();
        claim.Value.UserId.ShouldBe(user.Id);

        var usedAt = SqlProbe.ScalarText(
            Database,
            $"SELECT used_at::text FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}'");
        usedAt.ShouldNotBeEmpty("the claim is what stamps used_at");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}' "
                + "AND used_at IS NOT NULL")
            .ShouldBe(1);

        // The loser runs the same statement, matches no row, and therefore writes nothing: used_at is
        // still the first claim's value, never overwritten with a later one.
        var second = await HubTicketTestFlow.ClaimAsync(factory, issued.Ticket, Ct);
        second.IsError.ShouldBeTrue();
        SqlProbe.ScalarText(
                Database,
                $"SELECT used_at::text FROM hub_tickets WHERE ticket_hash = '{HubTicketSecret.HashOf(issued.Ticket)}'")
            .ShouldBe(usedAt);
    }

    [Fact]
    public async Task Claim_Returns_User_Of_The_Ticket_Owner()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var firstUser = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        var secondUser = await HubTicketTestFlow.CreateUserAsync(Database, Ct);

        using var firstClient = factory.ClientFor(firstUser.Id);
        using var secondClient = factory.ClientFor(secondUser.Id);

        var firstTicket = await HubTicketTestFlow.IssueAsync(firstClient, Ct);
        var secondTicket = await HubTicketTestFlow.IssueAsync(secondClient, Ct);

        var firstClaim = await HubTicketTestFlow.ClaimAsync(factory, firstTicket.Ticket, Ct);
        var secondClaim = await HubTicketTestFlow.ClaimAsync(factory, secondTicket.Ticket, Ct);

        firstClaim.IsError.ShouldBeFalse();
        secondClaim.IsError.ShouldBeFalse();

        firstClaim.Value.UserId.ShouldBe(firstUser.Id, "a ticket authenticates exactly the user it was issued to");
        firstClaim.Value.UserId.ShouldNotBe(secondUser.Id, "two users' tickets never cross");
        secondClaim.Value.UserId.ShouldBe(secondUser.Id);

        // The claim's role is the owner's live users.role, not a value baked into the ticket (WT37).
        firstClaim.Value.Role.ShouldBe(PlatformRole.User);

        SqlProbe.QueryStrings(
                Database,
                "SELECT ticket_hash || '|' || user_id::text FROM hub_tickets "
                + $"WHERE user_id IN ('{firstUser.Id}', '{secondUser.Id}') ORDER BY user_id::text")
            .ShouldBe(
                [
                    $"{HubTicketSecret.HashOf(firstTicket.Ticket)}|{firstUser.Id}",
                    $"{HubTicketSecret.HashOf(secondTicket.Ticket)}|{secondUser.Id}",
                ],
                ignoreOrder: true);
        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM hub_tickets WHERE used_at IS NOT NULL "
                + $"AND user_id IN ('{firstUser.Id}', '{secondUser.Id}')")
            .ShouldBe(2);
    }

    [Fact]
    public async Task User_Deletion_Cascades_To_Tickets()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var doomed = await HubTicketTestFlow.CreateUserAsync(Database, Ct);
        var survivor = await HubTicketTestFlow.CreateUserAsync(Database, Ct);

        using (var client = factory.ClientFor(doomed.Id))
        {
            await HubTicketTestFlow.IssueAsync(client, Ct);
            await HubTicketTestFlow.IssueAsync(client, Ct);
        }

        using (var client = factory.ClientFor(survivor.Id))
        {
            await HubTicketTestFlow.IssueAsync(client, Ct);
        }

        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE user_id = '{doomed.Id}'").ShouldBe(2);

        SqlProbe.Execute(Database, $"DELETE FROM users WHERE id = '{doomed.Id}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE user_id = '{doomed.Id}'")
            .ShouldBe(0, "ON DELETE CASCADE removes the account's tickets (BD11)");
        SqlProbe.Count(Database, $"SELECT count(*) FROM hub_tickets WHERE user_id = '{survivor.Id}'").ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM hub_tickets WHERE user_id IN ('{doomed.Id}', '{survivor.Id}')")
            .ShouldBe(1, "no orphan rows are left behind");
    }
}
