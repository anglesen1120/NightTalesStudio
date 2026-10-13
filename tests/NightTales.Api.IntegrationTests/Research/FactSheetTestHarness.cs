using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using NightTales.TestKit.Time;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// The shared world of the fact-sheet edit/approve tests (02.5.4-T1): one owner, one series, one episode, the
/// sources it cites and the fact-sheet versions it holds, plus the request helpers the four test classes
/// (GET, PUT, approve, concurrency) answer through. The seam is the real HTTP surface — the tests never build
/// an Application DTO — so what they assert is the wire contract of
/// <c>/api/v1/episodes/{id}/fact-sheet</c>.
/// </summary>
internal static class FactSheetHarness
{
    /// <summary>The clock of every seeded row (<c>FakeClock</c>), so stamps are deterministic.</summary>
    internal static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The token xUnit cancels the running test with.</summary>
    internal static CancellationToken Ct => TestContextToken.Value;

    /// <summary>The empty body of a PUT that saves no fact (<c>{"facts":[]}</c>).</summary>
    internal const string EmptyFactsBody = """{"facts":[]}""";

    /// <summary>A deterministic 64-character lowercase hex content hash (<paramref name="letter"/> repeated).</summary>
    internal static string Sha(char letter) => new(letter, 64);

    /// <summary>A new <c>ApiFactory</c> over the class database with the fixed clock.</summary>
    internal static ApiFactory Factory(TestDatabase database) => new(database, new FakeClock(Start));

    /// <summary>The strong ETag of a fact-sheet row (README D9: <c>"fs-&lt;fact_sheets.id&gt;"</c>).</summary>
    internal static string Etag(Guid factSheetId) => $"\"fs-{factSheetId}\"";

    /// <summary>
    /// Seeds one owner, one true-crime series and one episode in <paramref name="status"/> — the world every
    /// fact-sheet route addresses. No source and no sheet is seeded; each test adds exactly what it needs.
    /// </summary>
    internal static async Task<World> SeedAsync(
        TestDatabase database,
        EpisodeStatus status = EpisodeStatus.Draft,
        Topic topic = Topic.TrueCrime)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(database, actor).CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary>
    /// Adds a <c>url</c> source already <c>Fetched</c> with <paramref name="sha"/> (a real source for F13).
    /// <paramref name="finalUrl"/> models a redirect: the fetcher ends at one address while the source keeps the
    /// canonical <c>url</c> it was saved with, so a test can tell the two apart.
    /// </summary>
    internal static async Task<Guid> AddFetchedUrlAsync(
        TestDatabase database,
        World world,
        string? sha = null,
        string? title = "Article",
        string? finalUrl = null)
    {
        var (source, address) = BuildUrl(world);
        source.MarkFetched(finalUrl ?? address, title, "text/html", sha ?? Sha('a'), $"research/{Guid.CreateVersion7():N}.txt", 120, Start);

        return await SaveAsync(database, world, source);
    }

    /// <summary>Adds a <c>url</c> source still <c>Pending</c>: never fetched, so it has no content hash (F13).</summary>
    internal static async Task<Guid> AddPendingUrlAsync(TestDatabase database, World world)
    {
        var (source, _) = BuildUrl(world);

        return await SaveAsync(database, world, source);
    }

    /// <summary>Adds a <c>user_note</c> source, which is <c>Fetched</c> by construction but never a url source.</summary>
    internal static async Task<Guid> AddNoteAsync(
        TestDatabase database,
        World world,
        string text = "A true-crime note kept by the owner.")
    {
        var source = StorySource.CreateNote(Guid.CreateVersion7(), world.UserId, world.EpisodeId, text, Start);
        source.IsError.ShouldBeFalse();

        return await SaveAsync(database, world, source.Value);
    }

    /// <summary>
    /// Inserts one fact-sheet version of the world holding <paramref name="facts"/>: the alias map is built in
    /// first-citation order (<c>s1…sN</c>), exactly as the edit path reconciles it (CR-4), so a seeded version
    /// is indistinguishable from one a command wrote.
    /// </summary>
    internal static async Task<FactSheet> SeedSheetAsync(
        TestDatabase database,
        World world,
        int version,
        IReadOnlyList<FactSeed> facts,
        FactSheetStatus status = FactSheetStatus.Draft,
        int? maxFactId = null,
        Guid? approvedBy = null,
        DateTimeOffset? approvedAt = null)
    {
        var aliases = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var aliasesBySource = new Dictionary<Guid, string>();
        var items = new List<FactItem>(facts.Count);
        var lowestFree = 0;
        var highest = 0;

        foreach (var fact in facts)
        {
            var cited = new List<string>(fact.SourceIds.Count);
            foreach (var sourceId in fact.SourceIds)
            {
                if (aliasesBySource.TryGetValue(sourceId, out var known))
                {
                    cited.Add(known);
                    continue;
                }

                lowestFree++;
                var alias = $"s{lowestFree}";
                aliases[alias] = sourceId;
                aliasesBySource[sourceId] = alias;
                cited.Add(alias);
            }

            items.Add(new FactItem(fact.Id, fact.Statement, fact.SourceIds, cited, fact.SourceOverlap, fact.Confidence, fact.Origin));

            if (fact.Id.Length > 1
                && int.TryParse(fact.Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                highest = Math.Max(highest, number);
            }
        }

        return await new FactSheetBuilder(database, new TestCurrentUser { Id = world.UserId }, new FakeClock(Start))
            .CreateVersionAsync(
                world.UserId,
                world.EpisodeId,
                version,
                items,
                aliases,
                maxFactId ?? highest,
                status,
                approvedBy,
                approvedAt,
                ct: Ct);
    }

    /// <summary>The count of a <c>count(*)</c>-shaped probe against the class database.</summary>
    internal static long Count(TestDatabase database, string sql) => SqlProbe.Count(database, sql);

    /// <summary>
    /// The <c>fact_sheets</c> rows of the world as
    /// <c>version|status|max_fact_id|generation_run_id|prompt_version|created_by</c>, ordered by version.
    /// </summary>
    internal static List<string> SheetRows(TestDatabase database, World world) =>
        SqlProbe.QueryStrings(
            database,
            "SELECT version::text || '|' || status::text || '|' || max_fact_id::text || '|' "
            + "|| coalesce(generation_run_id::text, 'null') || '|' || prompt_version || '|' || created_by::text "
            + $"FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' ORDER BY version");

    /// <summary>The current (highest-version) sheet row id of the world, or <see cref="Guid.Empty"/>.</summary>
    internal static Guid CurrentSheetId(TestDatabase database, World world)
    {
        var id = SqlProbe.ScalarText(
            database,
            $"SELECT id::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' ORDER BY version DESC LIMIT 1");

        return id.Length == 0 ? Guid.Empty : Guid.Parse(id);
    }

    /// <summary>The number of rows of the world in <paramref name="status"/>.</summary>
    internal static long SheetsInStatus(TestDatabase database, World world, FactSheetStatus status) =>
        SqlProbe.Count(
            database,
            $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND status = {(short)status}");

    /// <summary>The fact ids of one version, read from the stored <c>facts</c> document.</summary>
    internal static List<string> StoredFactIds(TestDatabase database, World world, int version)
    {
        var json = SqlProbe.ScalarText(
            database,
            $"SELECT facts::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND version = {version}");

        var ids = new List<string>();
        using var document = JsonDocument.Parse(json);
        foreach (var fact in document.RootElement.EnumerateArray())
        {
            ids.Add(fact.GetProperty("id").GetString() ?? string.Empty);
        }

        return ids;
    }

    /// <summary>Builds the JSON body of a PUT from the input facts.</summary>
    internal static string Body(params FactInput[] facts)
    {
        var payload = new
        {
            facts = facts.Select(fact => new
            {
                id = fact.Id,
                statement = fact.Statement,
                sourceIds = fact.SourceIds,
                confidence = fact.Confidence,
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>One input fact of a PUT body (<c>Id</c> null = a new fact).</summary>
    internal static FactInput Fact(string? id, string statement, IReadOnlyList<Guid> sourceIds, string? confidence = null) =>
        new(id, statement, sourceIds, confidence);

    /// <summary>One stored fact of a seeded version.</summary>
    internal static FactSeed Seed(
        string id,
        string statement,
        IReadOnlyList<Guid> sourceIds,
        FactConfidence confidence = FactConfidence.High,
        FactOrigin origin = FactOrigin.Model,
        decimal? sourceOverlap = null) =>
        new(id, statement, sourceIds, confidence, origin, sourceOverlap);

    /// <summary><c>GET /api/v1/episodes/{id}/fact-sheet</c>.</summary>
    internal static Task<HttpResponseMessage> GetAsync(HttpClient client, Guid episodeId, string? acceptLanguage = null) =>
        SendAsync(client, HttpMethod.Get, FactSheetUrl(episodeId), null, null, acceptLanguage);

    /// <summary><c>PUT /api/v1/episodes/{id}/fact-sheet</c> with the given <c>If-Match</c> and body.</summary>
    internal static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        Guid episodeId,
        string? ifMatch,
        string body,
        string? acceptLanguage = null) =>
        SendAsync(client, HttpMethod.Put, FactSheetUrl(episodeId), ifMatch, body, acceptLanguage);

    /// <summary><c>POST /api/v1/episodes/{id}/fact-sheet/approve</c> with the given <c>If-Match</c>.</summary>
    internal static Task<HttpResponseMessage> ApproveAsync(
        HttpClient client,
        Guid episodeId,
        string? ifMatch,
        string? acceptLanguage = null) =>
        SendAsync(client, HttpMethod.Post, $"{FactSheetUrl(episodeId)}/approve", ifMatch, null, acceptLanguage);

    /// <summary>The route of the fact sheet of one episode.</summary>
    internal static string FactSheetUrl(Guid episodeId) => $"/api/v1/episodes/{episodeId}/fact-sheet";

    /// <summary>The response body as a detached <see cref="JsonElement"/>.</summary>
    internal static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    /// <summary>The problem body of an error response.</summary>
    internal static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem.ShouldNotBeNull();

        return problem;
    }

    /// <summary>Asserts the status and the <c>code</c> of a problem response.</summary>
    internal static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe((HttpStatusCode)status, text);
        (await ProblemAsync(response)).Code.ShouldBe(code);
    }

    /// <summary>The field-error keys of a problem response, in wire order.</summary>
    internal static IReadOnlyList<string> FieldKeys(ProblemResponse problem) =>
        problem.Errors is null ? [] : [.. problem.Errors.Keys];

    /// <summary>The <c>fs-</c> ETag the response header carries.</summary>
    internal static string ResponseEtag(HttpResponseMessage response)
    {
        response.Headers.ETag.ShouldNotBeNull();

        return response.Headers.ETag!.Tag;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? ifMatch,
        string? body,
        string? acceptLanguage)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (acceptLanguage is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        return await client.SendAsync(request, Ct);
    }

    private static (StorySource Source, string Address) BuildUrl(World world)
    {
        var address = $"https://example.com/article-{Guid.CreateVersion7():N}";
        var parsed = SourceUrl.Parse(address, new FakePublicSuffixList());
        parsed.IsError.ShouldBeFalse();

        var source = StorySource.CreateUrl(Guid.CreateVersion7(), world.UserId, world.EpisodeId, parsed.Value, Start);
        source.IsError.ShouldBeFalse();

        return (source.Value, address);
    }

    private static async Task<Guid> SaveAsync(TestDatabase database, World world, StorySource source)
    {
        await using var context = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        context.StorySources.Add(source);
        await context.SaveChangesAsync(Ct);

        return source.Id;
    }
}

/// <summary>One owner, one series and one episode: the world the fact-sheet routes address.</summary>
/// <param name="UserId">The owner (the tenant of every request).</param>
/// <param name="SeriesId">The series of the episode.</param>
/// <param name="EpisodeId">The episode.</param>
internal sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

/// <summary>A stored fact of a seeded version.</summary>
/// <param name="Id">The backend id (<c>f1…fN</c>).</param>
/// <param name="Statement">The stored statement.</param>
/// <param name="SourceIds">The cited source ids.</param>
/// <param name="Confidence">The stored confidence (T53).</param>
/// <param name="Origin">Who produced the statement.</param>
/// <param name="SourceOverlap">The 02.5.6 overlap score, or null when unscored.</param>
internal sealed record FactSeed(
    string Id,
    string Statement,
    IReadOnlyList<Guid> SourceIds,
    FactConfidence Confidence = FactConfidence.High,
    FactOrigin Origin = FactOrigin.Model,
    decimal? SourceOverlap = null);

/// <summary>One input fact of a PUT body.</summary>
/// <param name="Id">The cited existing fact id, or null for a new fact.</param>
/// <param name="Statement">The statement.</param>
/// <param name="SourceIds">The cited source ids (at least one unless the test asserts the failure).</param>
/// <param name="Confidence">The requested confidence, or null to keep the stored one (T53).</param>
internal sealed record FactInput(string? Id, string Statement, IReadOnlyList<Guid> SourceIds, string? Confidence = null);
