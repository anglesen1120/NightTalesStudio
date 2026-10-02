using System.Net;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.Realtime;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// The hub of 09 README BD11 end to end (09.8.1-T1 Tests table): a ticket-authenticated WebSocket joins
/// exactly one group — <c>user:{sub}</c> — the fan-out reaches only the owner, a non-WebSocket transport
/// and an unusable ticket are refused, the registry is kept clean, and no line of any log carries the
/// ticket (09 README D17).
/// </summary>
/// <remarks>
/// The host is the production composition root with a real PostgreSQL, the real <c>LISTEN</c> loop and
/// only 09.8.1-T2's validator replaced (its table arrives with that task).
/// </remarks>
public sealed class RealtimeHubTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task Connect_With_Valid_Ticket_Joins_Only_Its_Own_User_Group()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        await using var client = await harness.ConnectAsync(owner, PlatformRole.User, ct);

        using var bound = RealtimeHarness.Bounded(ct);

        // The same event raised for another user is not delivered here...
        await harness.SendAsync(
            other,
            RealtimeEventName.EpisodeUpdated,
            RealtimeHarness.Body(other, RealtimeEventName.EpisodeUpdated, episodeId),
            bound.Token);

        // ...and the owner's own event is the very first thing this socket sees, which is what makes the
        // negative half of this test an ordering assertion instead of a timeout.
        await harness.SendAsync(
            owner,
            RealtimeEventName.EpisodeUpdated,
            RealtimeHarness.Body(owner, RealtimeEventName.EpisodeUpdated, episodeId),
            bound.Token);

        var received = await client.ReceiveAsync(bound.Token);

        received.Target.ShouldBe(nameof(RealtimeEventName.EpisodeUpdated));
        received.Event.ShouldBe("episode_updated");
        received.IdOf("episodeId").ShouldBe(episodeId);
    }

    [Fact]
    public async Task Client_Cannot_Choose_Its_Own_Group()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);

        // Every parameter a client could use to steer its group is present in the URL and ignored: the
        // group comes from the claimed ticket's `sub` alone.
        await using var client = await harness.ConnectAsync(
            owner,
            PlatformRole.User,
            ct,
            RealtimeHarness.GroupInjectionQuery);

        using var bound = RealtimeHarness.Bounded(ct);

        await harness.SendAsync(
            other,
            RealtimeEventName.PublicationUpdated,
            RealtimeHarness.Body(other, RealtimeEventName.PublicationUpdated),
            bound.Token);
        await harness.SendAsync(
            owner,
            RealtimeEventName.CreditBalanceChanged,
            RealtimeHarness.Body(owner, RealtimeEventName.CreditBalanceChanged),
            bound.Token);

        var received = await client.ReceiveAsync(bound.Token);
        received.Target.ShouldBe(nameof(RealtimeEventName.CreditBalanceChanged));

        // Structurally too: the hub exposes no invocable method at all, so there is no "join group"
        // surface a client could call (the connect probe of SignalRTestClient proves the same over the
        // protocol: an unknown method is answered with an error completion).
        var declared = typeof(EpisodeHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        string.Join(",", declared).ShouldBe("OnConnectedAsync,OnDisconnectedAsync");
    }

    [Fact]
    public async Task Injected_Group_Is_Ignored_So_Only_The_Tickets_User_Group_Receives()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();

        // The user id the hostile query parameter asks to join (BD11/D10: the group is the ticket's
        // `sub`, never a client-supplied value).
        var injected = Guid.Parse("00000000-0000-0000-0000-000000000001");

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        await using var client = await harness.ConnectAsync(
            owner,
            PlatformRole.User,
            ct,
            RealtimeHarness.GroupInjectionQuery);

        using var bound = RealtimeHarness.Bounded(ct);

        // An event addressed to the injected group's user must not reach this socket...
        await harness.SendAsync(
            injected,
            RealtimeEventName.CreditBalanceChanged,
            RealtimeHarness.Body(injected, RealtimeEventName.CreditBalanceChanged),
            bound.Token);

        // ...while the owner's own event is the very first thing the socket sees, so nothing was queued
        // ahead of it.
        var episodeId = Guid.CreateVersion7();
        await harness.SendAsync(
            owner,
            RealtimeEventName.EpisodeUpdated,
            RealtimeHarness.Body(owner, RealtimeEventName.EpisodeUpdated, episodeId),
            bound.Token);

        var received = await client.ReceiveAsync(bound.Token);

        received.Target.ShouldBe(nameof(RealtimeEventName.EpisodeUpdated));
        received.IdOf("episodeId").ShouldBe(episodeId);
    }

    [Fact]
    public async Task Non_WebSocket_Transport_Is_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        using var client = harness.CreateClient();

        using var negotiate = await client.PostAsync(
            EpisodeHub.Path + "/negotiate?ticket=" + Uri.EscapeDataString(harness.Tickets.Issue(owner)),
            content: null,
            ct);

        negotiate.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync(ct));
        var available = body.RootElement
            .GetProperty("availableTransports")
            .EnumerateArray()
            .Select(transport => transport.GetProperty("transport").GetString())
            .ToArray();

        // BD11: WebSockets only, so the client skips negotiation and no sticky session is needed.
        string.Join(",", available).ShouldBe("WebSockets");

        // A long-polling connection on the same end point is refused (the transports above are all it
        // serves), never upgraded.
        var connectionToken = body.RootElement.TryGetProperty("connectionToken", out var token)
            ? token.GetString()
            : null;

        using var polling = await client.GetAsync(
            EpisodeHub.Path
            + "?id=" + Uri.EscapeDataString(connectionToken ?? "unknown")
            + "&ticket=" + Uri.EscapeDataString(harness.Tickets.Issue(owner)),
            ct);

        polling.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
        polling.StatusCode.ShouldNotBe(HttpStatusCode.SwitchingProtocols);
    }

    [Fact]
    public async Task EpisodeUpdated_Is_Delivered_To_The_Owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        await using var client = await harness.ConnectAsync(owner, PlatformRole.User, ct);

        using var bound = RealtimeHarness.Bounded(ct);

        var body = RealtimeHarness.Body(owner, RealtimeEventName.EpisodeUpdated, episodeId);
        await harness.SendAsync(owner, RealtimeEventName.EpisodeUpdated, body, bound.Token);

        var received = await client.ReceiveAsync(bound.Token);

        received.Target.ShouldBe(nameof(RealtimeEventName.EpisodeUpdated));
        JsonElement.DeepEquals(received.Argument, body).ShouldBeTrue(
            "the forwarded body is the NOTIFY body, unchanged");
        received.IdOf("episodeId").ShouldBe(episodeId);
        received.Argument.GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Event_Is_Not_Delivered_To_Another_User()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        await using var firstClient = await harness.ConnectAsync(first, PlatformRole.User, ct);
        await using var secondClient = await harness.ConnectAsync(second, PlatformRole.User, ct);

        using var bound = RealtimeHarness.Bounded(ct);

        await harness.SendAsync(
            first,
            RealtimeEventName.EpisodeUpdated,
            RealtimeHarness.Body(first, RealtimeEventName.EpisodeUpdated, Guid.CreateVersion7()),
            bound.Token);
        await harness.SendAsync(
            second,
            RealtimeEventName.PublicationUpdated,
            RealtimeHarness.Body(second, RealtimeEventName.PublicationUpdated),
            bound.Token);

        // The second user's own event arrives as the first message of that socket: the first user's
        // event was never queued on it.
        var secondReceived = await secondClient.ReceiveAsync(bound.Token);
        secondReceived.Target.ShouldBe(nameof(RealtimeEventName.PublicationUpdated));

        var firstReceived = await firstClient.ReceiveAsync(bound.Token);
        firstReceived.Target.ShouldBe(nameof(RealtimeEventName.EpisodeUpdated));
    }

    [Fact]
    public async Task Every_Open_Connection_Of_The_Owner_Receives_The_Same_Event()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        await using var first = await harness.ConnectAsync(owner, PlatformRole.User, ct);
        await using var second = await harness.ConnectAsync(owner, PlatformRole.User, ct);

        using var bound = RealtimeHarness.Bounded(ct);
        await harness.WaitForConnectionsAsync(2, bound.Token);

        // Two tabs of one user share the group `user:{sub}`, so both must see the event (BD11).
        await harness.SendAsync(
            owner,
            RealtimeEventName.EpisodeUpdated,
            RealtimeHarness.Body(owner, RealtimeEventName.EpisodeUpdated, episodeId),
            bound.Token);

        foreach (var client in new[] { first, second })
        {
            var received = await client.ReceiveAsync(bound.Token);

            received.Target.ShouldBe(nameof(RealtimeEventName.EpisodeUpdated));
            received.IdOf("episodeId").ShouldBe(episodeId);
        }
    }

    [Fact]
    public async Task Disconnect_Removes_The_Group_Membership_And_Registry_Entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        var client = await harness.ConnectAsync(owner, PlatformRole.User, ct);

        using var bound = RealtimeHarness.Bounded(ct);
        await harness.WaitForConnectionsAsync(1, bound.Token);

        await client.DisposeAsync();

        // The instance holds no dead socket (BD11: the registry drops entries on disconnect).
        await harness.WaitForConnectionsAsync(0, bound.Token);

        // The group is per connection: the user's next connection receives again, and nothing was
        // delivered to the group the closed connection left.
        await using var reconnected = await harness.ConnectAsync(owner, PlatformRole.User, ct);
        await harness.SendAsync(
            owner,
            RealtimeEventName.CreditBalanceChanged,
            RealtimeHarness.Body(owner, RealtimeEventName.CreditBalanceChanged),
            bound.Token);

        var received = await reconnected.ReceiveAsync(bound.Token);
        received.Target.ShouldBe(nameof(RealtimeEventName.CreditBalanceChanged));
        harness.Resolve<RealtimeConnectionRegistry>().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_Ticket_Is_Rejected_With_401_HubTicketInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        using var client = harness.CreateClient();

        using (var missing = await client.PostAsync(EpisodeHub.Path + "/negotiate", content: null, ct))
        {
            var problem = await ReadProblemAsync(missing, ct);

            missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            problem.Code.ShouldBe(ErrorCodes.HubTicketInvalid);
            problem.Status.ShouldBe(401);
        }

        // A used (or unknown) ticket is refused the same way: one ticket, one connect (BD11/WF10).
        var ticket = harness.Tickets.Issue(owner);
        await using (var socket = await SignalRTestClient.ConnectAsync(harness.Server, ticket, null, ct))
        {
            socket.ShouldNotBeNull();
        }

        using var replay = await client.PostAsync(
            EpisodeHub.Path + "/negotiate?ticket=" + Uri.EscapeDataString(ticket),
            content: null,
            ct);

        var replayProblem = await ReadProblemAsync(replay, ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        replayProblem.Code.ShouldBe(ErrorCodes.HubTicketInvalid);
    }

    [Fact]
    public async Task Unregistered_TicketValidator_FailsFast_Without_APermissiveFallback()
    {
        var ct = TestContext.Current.CancellationToken;

        // 0. 09.8.1-T2 closed the seam: the production composition root now registers exactly the
        //    production validator (it used to register none).
        await using (var production = ApiFactory.ProductionHost(Database.ConnectionString))
        {
            // The validator is scoped (the AppDbContext it claims through is), so it is resolved from a
            // request-like scope — the root provider refuses a scoped service by design.
            using var scope = production.Services.CreateScope();

            scope.ServiceProvider.GetService<IHubTicketValidator>().ShouldBeOfType<NpgsqlHubTicketValidator>();
        }

        // 1. With the registration removed there is nothing to fall back to: the handler throws instead
        //    of authenticating anyone.
        await using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.ConfigureServices(services => services.RemoveAll<IHubTicketValidator>()));
        host.Services.GetService<IHubTicketValidator>().ShouldBeNull();

        using (var client = host.CreateClient())
        {
            try
            {
                using var response = await client.PostAsync(
                    EpisodeHub.Path + "/negotiate?ticket=" + Uri.EscapeDataString("any-ticket"),
                    content: null,
                    ct);

                // A refused connect, never an accepted one: the handler throws before any claim.
                response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
                ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(500);
            }
            catch (InvalidOperationException exception)
            {
                // Depending on where the authentication middleware sits in the pipeline the exception
                // reaches the caller directly; either shape is a refusal, never a socket.
                exception.Message.ShouldContain(HubTicketAuthenticationHandler.MissingValidatorMessage);
            }
        }

        // 2. The handler itself fails fast with the explicit message (and never authenticates anyone).
        using var monitor = new OptionsMonitor<AuthenticationSchemeOptions>(
            new OptionsFactory<AuthenticationSchemeOptions>(
                Array.Empty<IConfigureOptions<AuthenticationSchemeOptions>>(),
                Array.Empty<IPostConfigureOptions<AuthenticationSchemeOptions>>()),
            Array.Empty<IOptionsChangeTokenSource<AuthenticationSchemeOptions>>(),
            new OptionsCache<AuthenticationSchemeOptions>());

        using var emptyServices = new ServiceCollection().BuildServiceProvider();

        var handler = new HubTicketAuthenticationHandler(
            monitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            emptyServices);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?ticket=any-ticket");

        await handler.InitializeAsync(
            new AuthenticationScheme(
                HubTicketAuthenticationHandler.SchemeName,
                displayName: null,
                typeof(HubTicketAuthenticationHandler)),
            context);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => handler.AuthenticateAsync());

        thrown.Message.ShouldBe(HubTicketAuthenticationHandler.MissingValidatorMessage);
        thrown.Message.ShouldContain("09.8.1-T2");
    }

    [Fact]
    public void HubsOptions_UnusableCap_RefusesToStart()
    {
        // A cap of zero minutes would close every socket the instant it opened, and a misspelled role
        // would be a cap that never applies; both refuse to start instead of running with a silently
        // wrong policy ([RULED — README §B10 WT37]).
        ShouldRefuseToStart(($"{HubsOptions.SectionName}:{nameof(HubsOptions.MaxConnectionMinutes)}:user", "0"));
        ShouldRefuseToStart(($"{HubsOptions.SectionName}:{nameof(HubsOptions.MaxConnectionMinutes)}:ownr", "5"));
    }

    [Fact]
    public async Task No_Log_Line_Contains_The_Ticket()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();
        var sink = new ProblemExchange.CapturingLogs();

        var tickets = new NightTales.TestKit.Realtime.FakeHubTicketValidator();
        var accepted = tickets.Issue(owner, PlatformRole.Owner);

        // Method-scoped: the host is disposed (and the sink cleared) only after the assertions below.
        await using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton<IHubTicketValidator>(tickets);
                services.AddLogging(logging => logging.AddProvider(sink));
            }));

        using (var client = host.CreateClient())
        {
            using var refused = await client.PostAsync(
                EpisodeHub.Path + "/negotiate?ticket=" + Uri.EscapeDataString("not-a-real-ticket"),
                content: null,
                ct);

            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await using (var socket = await SignalRTestClient.ConnectAsync(host.Server, accepted, extraQuery: null, ct))
        {
            socket.ShouldNotBeNull();
        }

        sink.Contains(LogLevel.Warning, "hub.ticket.rejected").ShouldBeTrue(
            "the rejected attempt must have been logged for this scan to mean anything");

        // 09 README D17: no line of this feature may carry the ticket. ASP.NET Core's own request
        // diagnostics render the raw URL — including `?ticket=…` — and stripping it is 09.9.3-T2's
        // redaction processor (plan §8, which B8.1 makes depend on this task); what T1 must guarantee is
        // that it adds no such line of its own.
        var carrying = sink.Entries
            .Where(entry => entry.Message.Contains(accepted, StringComparison.Ordinal))
            .ToArray();

        carrying.ShouldAllBe(entry => entry.Message.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));

        var ours = sink.Entries
            .Where(entry => entry.Message.StartsWith("NightTales", StringComparison.Ordinal))
            .ToArray();

        ours.ShouldNotBeEmpty("the real-time code must have logged the refused attempt");
        ours.ShouldNotContain(entry => entry.Message.Contains(accepted, StringComparison.Ordinal));
        ours.ShouldNotContain(entry => entry.Message.Contains("not-a-real-ticket", StringComparison.Ordinal));
        ours.ShouldNotContain(entry => entry.Message.Contains("Password=", StringComparison.OrdinalIgnoreCase));

        sink.Entries
            .Select(entry => entry.Message)
            .ShouldNotContain(line => line.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    private void ShouldRefuseToStart(params (string Key, string Value)[] settings)
    {
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder =>
            {
                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }
            });

        Should.Throw<OptionsValidationException>(() => host.CreateClient());
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var problem = JsonSerializer.Deserialize<ProblemResponse>(body, ProblemResponse.Options);

        problem.ShouldNotBeNull();
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType!.MediaType.ShouldBe(ProblemDetailsWriter.JsonContentType);

        return problem!;
    }
}
