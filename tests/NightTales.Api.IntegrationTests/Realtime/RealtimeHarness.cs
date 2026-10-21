using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Api.Realtime;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit.Api;
using NightTales.TestKit.Realtime;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// The Api in-process with a real PostgreSQL and 09.8.1-T2's validator replaced by
/// <see cref="FakeHubTicketValidator"/> (09.8.1-T1 design, "seam with 09.8.1-T2"), so the hub, the
/// <c>LISTEN</c> loop and the fan-out are exercised over the production composition root.
/// </summary>
/// <remarks>
/// Nothing else differs from production: the host is the one built from <c>Program</c>, its own
/// <c>RealtimeListenerService</c> opens the one <c>LISTEN</c> session of this instance against this
/// test's database, and the hub is reached over the TestServer's WebSocket transport. The host is held as
/// <see cref="IAsyncDisposable"/> because this assembly may not name <c>Program</c> (09.2.1-T2's CS0433
/// note).
/// </remarks>
internal sealed class RealtimeHarness : IAsyncDisposable
{
    /// <summary>
    /// Query parameters a hostile client could add to pick a group; the hub may read none of them (the
    /// ticket's <c>sub</c> is the only source of the group).
    /// </summary>
    public const string GroupInjectionQuery =
        "group=user%3A00000000-0000-0000-0000-000000000001&userIdentifier=attacker&userId=attacker";

    private readonly HttpClient _client;
    private readonly IAsyncDisposable _host;
    private readonly IServiceProvider _services;

    private RealtimeHarness(
        IAsyncDisposable host,
        HttpClient client,
        IServiceProvider services,
        TestServer server,
        FakeHubTicketValidator tickets)
    {
        _host = host;
        _client = client;
        _services = services;
        Server = server;
        Tickets = tickets;
    }

    /// <summary>The single-use ticket issuer the host's <c>HubTicket</c> scheme consumes from.</summary>
    public FakeHubTicketValidator Tickets { get; }

    /// <summary>The transport of the production host, for a client that must present a chosen ticket.</summary>
    public TestServer Server { get; }

    /// <summary>Starts the production host with the fake validator registered.</summary>
    /// <param name="connectionString">The test database; the listener <c>LISTEN</c>s on it too.</param>
    public static RealtimeHarness Start(string connectionString)
    {
        var tickets = new FakeHubTicketValidator();

        var host = ApiFactory.ProductionHost(
            connectionString,
            builder => builder.ConfigureServices(
                services => services.AddSingleton<IHubTicketValidator>(tickets)));

        return new RealtimeHarness(host, host.CreateClient(), host.Services, host.Server, tickets);
    }

    /// <summary>
    /// A copy of <paramref name="cancellationToken"/> that gives up after 30 seconds, so a delivery that
    /// never arrives fails the test instead of hanging the run.
    /// </summary>
    public static CancellationTokenSource Bounded(CancellationToken cancellationToken)
    {
        var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30));

        return bound;
    }

    /// <summary>The NOTIFY body of one event, built by the production builder.</summary>
    public static JsonElement Body(Guid userId, RealtimeEventName name, Guid? episodeId = null)
    {
        using var document = JsonDocument.Parse(
            RealtimePayload.Build(userId, new RealtimeEvent(name, new RealtimeIds(EpisodeId: episodeId))));

        return document.RootElement.Clone();
    }

    /// <summary>An HTTP client of the production host.</summary>
    public HttpClient CreateClient() => _client;

    /// <summary>Resolves a service of the production host.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    public T Resolve<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>Connects a socket as <paramref name="userId"/> at <paramref name="role"/>.</summary>
    /// <param name="userId">The user the issued ticket names.</param>
    /// <param name="role">The role the issued ticket carries.</param>
    /// <param name="cancellationToken">The test's bound.</param>
    /// <param name="extraQuery">Extra query parameters a hostile client might add.</param>
    public async Task<SignalRTestClient> ConnectAsync(
        Guid userId,
        PlatformRole role,
        CancellationToken cancellationToken,
        string? extraQuery = null) =>
        await SignalRTestClient.ConnectAsync(Server, Tickets.Issue(userId, role), extraQuery, cancellationToken);

    /// <summary>
    /// Sends one event to <paramref name="userId"/>'s group through the host's own sender, i.e. exactly
    /// the way the listener forwards a message.
    /// </summary>
    public Task SendAsync(
        Guid userId,
        RealtimeEventName name,
        JsonElement body,
        CancellationToken cancellationToken) =>
        Resolve<IRealtimeGroupSender>().SendAsync(userId, name, body, cancellationToken);

    /// <summary>
    /// The notifier on the caller's own context, as a command handler or an outbox handler calls it: the
    /// host's real options and metrics, this test's <see cref="AppDbContext"/>.
    /// </summary>
    public PgNotifyRealtimeNotifier NotifierOn(AppDbContext context) =>
        new(
            context,
            Resolve<IOptions<RealtimeOptions>>(),
            Resolve<RealtimeMetrics>(),
            NullLogger<PgNotifyRealtimeNotifier>.Instance);

    /// <summary>
    /// Completes once this instance's <c>LISTEN</c> session is open — <c>LISTEN realtime; LISTEN
    /// security;</c> have both been issued, so a notification committed after this point is delivered.
    /// </summary>
    public Task ListenerReadyAsync(CancellationToken cancellationToken) =>
        Resolve<RealtimeListenerService>().ListeningAsync(cancellationToken);

    /// <summary>
    /// Waits (bounded, 10 ms apart) until the instance's registry holds <paramref name="expected"/>
    /// connections. The hub registers a socket inside <c>OnConnectedAsync</c> and drops it inside
    /// <c>OnDisconnectedAsync</c>, so this is the observable of both transitions — and the caller's token
    /// is what keeps the wait from being an unbounded sleep.
    /// </summary>
    public async Task WaitForConnectionsAsync(int expected, CancellationToken cancellationToken)
    {
        var registry = Resolve<RealtimeConnectionRegistry>();

        while (registry.Count != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }
}
