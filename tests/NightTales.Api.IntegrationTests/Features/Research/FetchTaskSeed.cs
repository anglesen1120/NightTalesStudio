using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// Seeds the rows of the fetcher's internal API tests (02.5.2-T2) and speaks its wire format. Rows are inserted with SQL
/// and every time is the database clock (<c>now()</c>), the clock the lease statements themselves compare with, so a
/// lease that "expires in the past" really is one for the statement under test. Every API test of the three classes calls
/// <see cref="ResetTasks"/> first: the caps are platform-wide, so a lease left over from an earlier test of the same class
/// database would change the arithmetic.
/// </summary>
/// <param name="database">The class database.</param>
internal sealed class FetchTaskSeed(TestDatabase database)
{
    /// <summary>The route prefix of the internal API.</summary>
    public const string Prefix = "/internal/fetcher/v1/tasks";

    /// <summary>The worker id most tests lease with.</summary>
    public const string Worker = "fetcher-1";

    /// <summary>The clock every test host runs on; only the stamps the Api itself writes (<c>fetched_at</c>) come from it.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContextToken.Value;

    /// <summary>Seeds one user, series (<c>vi</c>, <c>ghost</c>) and episode.</summary>
    public async Task<World> SeedWorldAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(database, actor, new FakeClock(Start)).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(database, actor, new FakeClock(Start)).CreateAsync(user.Id, series.Id, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary>Deletes every fetch task so the platform-wide caps start from zero.</summary>
    public void ResetTasks() => SqlProbe.Execute(database, "DELETE FROM fetch_tasks");

    /// <summary>Inserts a pending url source and returns its id.</summary>
    /// <param name="world">The owner.</param>
    /// <param name="domain">The registrable domain.</param>
    /// <param name="path">The URL path, with the leading slash; defaults to a unique one.</param>
    public Guid AddSource(World world, string domain, string? path = null)
    {
        var id = Guid.NewGuid();
        var url = $"https://{domain}{path ?? "/" + id.ToString("N")}";
        SqlProbe.Execute(
            database,
            "INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, created_at) "
            + $"VALUES ('{id}', '{world.UserId}', '{world.EpisodeId}', 'url', '{url}', '{url}', '{domain}', 'pending', now())");

        return id;
    }

    /// <summary>Inserts a source and a queued <c>fetch_source</c> task for it; the older the task, the earlier it is leased.</summary>
    /// <param name="world">The owner.</param>
    /// <param name="domain">The registrable domain.</param>
    /// <param name="ageSeconds">How long ago the task became eligible.</param>
    /// <param name="path">The URL path of the source.</param>
    public SeededTask AddQueued(World world, string domain, int ageSeconds = 60, string? path = null)
    {
        var source = AddSource(world, domain, path);

        return InsertTask(world, source, domain, "queued", ageSeconds, attempts: 0, leasedBy: null, leaseUntil: null);
    }

    /// <summary>Inserts a source and a <c>fetch_source</c> task already leased by <paramref name="workerId"/>.</summary>
    /// <param name="world">The owner.</param>
    /// <param name="domain">The registrable domain.</param>
    /// <param name="workerId">The lease holder.</param>
    /// <param name="leaseUntil">A SQL expression for <c>lease_until</c>.</param>
    /// <param name="attempts">The attempts already counted.</param>
    /// <param name="path">The URL path of the source.</param>
    public SeededTask AddLeased(
        World world,
        string domain,
        string workerId = Worker,
        string leaseUntil = "now() + interval '90 seconds'",
        int attempts = 1,
        string? path = null)
    {
        var source = AddSource(world, domain, path);

        return InsertTask(world, source, domain, "leased", 10, attempts, workerId, leaseUntil);
    }

    /// <summary>Queues a <c>find_sources</c> run through the aggregate, as the 02.7 handler will.</summary>
    /// <param name="world">The owner.</param>
    public async Task<Guid> AddFindSourcesRunAsync(World world)
    {
        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        var run = GenerationRun.Queue(
                world.UserId,
                world.SeriesId,
                world.EpisodeId,
                GenerationKind.FindSources,
                GenerationTrigger.User,
                null,
                EpisodeStatus.Draft,
                null,
                null,
                0m,
                Start)
            .Value;
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(Ct);

        return run.Id;
    }

    /// <summary>Inserts a <c>find_sources</c> task (no source, no url, no domain) for <paramref name="runId"/>.</summary>
    /// <param name="world">The owner.</param>
    /// <param name="runId">The generation run the task belongs to.</param>
    /// <param name="leasedBy">The lease holder, or null for a queued task.</param>
    public Guid AddFindSourcesTask(World world, Guid runId, string? leasedBy = null)
    {
        var id = Guid.NewGuid();
        var state = leasedBy is null
            ? "'queued', 0, NULL, NULL, NULL"
            : $"'leased', 1, '{leasedBy}', now() - interval '5 seconds', now() + interval '90 seconds'";
        SqlProbe.Execute(
            database,
            "INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, generation_run_id, kind, url, registrable_domain, "
            + "status, attempts, leased_by, leased_at, lease_until, not_before, created_at) "
            + $"VALUES ('{id}', '{world.UserId}', '{world.EpisodeId}', NULL, '{runId}', 'find_sources', NULL, NULL, "
            + $"{state}, now() - interval '1 minute', now() - interval '1 minute')");

        return id;
    }

    /// <summary>Runs a statement that returns one value and returns it as text (null becomes empty).</summary>
    /// <param name="sql">The statement.</param>
    public string Text(string sql) => SqlProbe.ScalarText(database, sql);

    /// <summary>Runs a count statement.</summary>
    /// <param name="sql">The statement.</param>
    public long Count(string sql) => SqlProbe.Count(database, sql);

    /// <summary>Runs a statement that changes rows.</summary>
    /// <param name="sql">The statement.</param>
    public void Execute(string sql) => SqlProbe.Execute(database, sql);

    /// <summary>The number of tasks currently leased.</summary>
    public long LeasedCount() => Count("SELECT count(*) FROM fetch_tasks WHERE status = 'leased'");

    /// <summary>The status of one task.</summary>
    /// <param name="task">The task id.</param>
    public string TaskStatus(Guid task) => Text($"SELECT status FROM fetch_tasks WHERE id = '{task}'");

    /// <summary>The status of one source.</summary>
    /// <param name="source">The source id.</param>
    public string SourceStatus(Guid source) => Text($"SELECT status FROM story_sources WHERE id = '{source}'");

    /// <summary>The hex SHA-256 of the UTF-8 form of <paramref name="text"/>.</summary>
    /// <param name="text">The text.</param>
    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A body for <c>POST .../lease</c>.</summary>
    /// <param name="workerId">The worker id.</param>
    /// <param name="max">The requested batch size.</param>
    public static Dictionary<string, object?> LeaseBody(string? workerId = Worker, int max = 4) =>
        new() { ["workerId"] = workerId, ["max"] = max };

    /// <summary>A valid <c>done</c> body for a <c>fetch_source</c> task; any member can be overridden or removed afterwards.</summary>
    /// <param name="text">The extracted text; the hash is computed over it.</param>
    /// <param name="workerId">The lease holder.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    public static Dictionary<string, object?> DoneBody(
        string text = "Một bài báo thử nghiệm.\nDòng thứ hai.",
        string workerId = Worker,
        string finalUrl = "https://news.example.com/final-article") =>
        new()
        {
            ["workerId"] = workerId,
            ["status"] = "done",
            ["httpStatus"] = 200,
            ["finalUrl"] = finalUrl,
            ["title"] = "Tiêu đề thử",
            ["contentType"] = "text/html; charset=utf-8",
            ["text"] = text,
            ["textSha256"] = Sha256(text),
        };

    /// <summary>The blob calls that touched the research text prefix; the storage prober's own <c>_health</c> traffic is not one.</summary>
    /// <param name="blobs">The recording store.</param>
    public static IReadOnlyList<NightTales.TestKit.Storage.BlobCall> ResearchCalls(NightTales.TestKit.Storage.InMemoryBlobStore blobs) =>
        [.. blobs.Calls.Where(call => call.Key.StartsWith("research/", StringComparison.Ordinal))];

    /// <summary>How many research text objects were written.</summary>
    /// <param name="blobs">The recording store.</param>
    public static int ResearchPuts(NightTales.TestKit.Storage.InMemoryBlobStore blobs) =>
        blobs.Calls.Count(call => call.Operation == NightTales.TestKit.Storage.BlobOperation.Put && call.Key.StartsWith("research/", StringComparison.Ordinal));

    /// <summary>A <c>failed</c> body.</summary>
    /// <param name="errorCode">The code the fetcher reports.</param>
    /// <param name="workerId">The lease holder.</param>
    public static Dictionary<string, object?> FailedBody(string? errorCode, string workerId = Worker) =>
        new() { ["workerId"] = workerId, ["status"] = "failed", ["httpStatus"] = 403, ["errorCode"] = errorCode };

    /// <summary>The URL of one task route.</summary>
    /// <param name="taskId">The task.</param>
    /// <param name="segment"><c>heartbeat</c> or <c>result</c>.</param>
    public static string Route(Guid taskId, string segment) => $"{Prefix}/{taskId}/{segment}";

    /// <summary>Posts a JSON body.</summary>
    /// <param name="client">The client.</param>
    /// <param name="path">The route.</param>
    /// <param name="body">The body.</param>
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body, Web, Ct);

    /// <summary>Posts a raw JSON text, so a test can send what the serializer would refuse to produce.</summary>
    /// <param name="client">The client.</param>
    /// <param name="path">The route.</param>
    /// <param name="json">The body text.</param>
    public static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    /// <summary>Leases and returns the response (status and body).</summary>
    /// <param name="client">The internal client.</param>
    /// <param name="workerId">The worker id.</param>
    /// <param name="max">The requested batch size.</param>
    public static Task<HttpResponseMessage> LeaseAsync(HttpClient client, string? workerId = Worker, int max = 4) =>
        PostAsync(client, Prefix + "/lease", LeaseBody(workerId, max));

    /// <summary>Leases, requires a 200 and returns the array.</summary>
    /// <param name="client">The internal client.</param>
    /// <param name="workerId">The worker id.</param>
    /// <param name="max">The requested batch size.</param>
    public static async Task<JsonElement[]> LeaseItemsAsync(HttpClient client, string workerId = Worker, int max = 4)
    {
        using var response = await LeaseAsync(client, workerId, max);
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"lease answered {(int)response.StatusCode}: {text}");
        }

        return [.. JsonDocument.Parse(text).RootElement.EnumerateArray().Select(item => item.Clone())];
    }

    /// <summary>Reads a response body as JSON.</summary>
    /// <param name="response">The response.</param>
    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>The <c>code</c> of a problem response, after checking its media type.</summary>
    /// <param name="response">The response.</param>
    public static async Task<string> CodeOfAsync(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != "application/problem+json")
        {
            throw new InvalidOperationException(
                $"expected application/problem+json but got {mediaType} ({(int)response.StatusCode}): "
                + await response.Content.ReadAsStringAsync(Ct));
        }

        return (await ReadJsonAsync(response)).GetProperty("code").GetString()!;
    }

    private SeededTask InsertTask(
        World world,
        Guid source,
        string domain,
        string status,
        int ageSeconds,
        int attempts,
        string? leasedBy,
        string? leaseUntil)
    {
        var id = Guid.NewGuid();
        var url = SqlProbe.ScalarText(database, $"SELECT url FROM story_sources WHERE id = '{source}'");
        var leaseColumns = leasedBy is null
            ? "NULL, NULL, NULL"
            : $"'{leasedBy}', now() - interval '5 seconds', {leaseUntil}";
        SqlProbe.Execute(
            database,
            "INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, kind, url, registrable_domain, status, attempts, "
            + "leased_by, leased_at, lease_until, not_before, created_at) "
            + $"VALUES ('{id}', '{world.UserId}', '{world.EpisodeId}', '{source}', 'fetch_source', '{url}', '{domain}', "
            + $"'{status}', {attempts.ToString(CultureInfo.InvariantCulture)}, {leaseColumns}, "
            + $"now() - make_interval(secs => {ageSeconds.ToString(CultureInfo.InvariantCulture)}), "
            + $"now() - make_interval(secs => {ageSeconds.ToString(CultureInfo.InvariantCulture)}))");

        return new SeededTask(id, source, domain, url);
    }

    /// <summary>One user's series and episode.</summary>
    /// <param name="UserId">The owner.</param>
    /// <param name="SeriesId">The series.</param>
    /// <param name="EpisodeId">The episode.</param>
    public sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    /// <summary>A seeded <c>fetch_source</c> task and its source.</summary>
    /// <param name="TaskId">The task.</param>
    /// <param name="SourceId">The source.</param>
    /// <param name="Domain">The registrable domain.</param>
    /// <param name="Url">The source URL.</param>
    public sealed record SeededTask(Guid TaskId, Guid SourceId, string Domain, string Url);
}
