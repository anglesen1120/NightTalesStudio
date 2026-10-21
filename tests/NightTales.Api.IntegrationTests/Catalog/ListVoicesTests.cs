using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Storage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Catalog;

/// <summary>
/// <c>GET /api/v1/voices</c> against the real host and a real PostgreSQL 17 (09.4.2 plan §9): the active
/// filter, the language filter, the public preview URL, the cache headers and the ETag/304 pair.
/// </summary>
/// <remarks>
/// <para>
/// The class database is shared by every test of this class and a Development host seeds the dev catalog
/// on start (09.2.5), so no test asserts a hard-coded total: each one seeds rows under ids no other test
/// uses and compares the answer with what the database itself reports.
/// </para>
/// <para>
/// A catalog route is not user-owned (D10 filters user rows only), so a test needs no <c>users</c> row —
/// only an authenticated caller, which the test authentication scheme provides from the user id.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ListVoicesTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Route = "/api/v1/voices";

    /// <summary>The public bucket base URL the preview tests configure the host with.</summary>
    private const string PublicBaseUrl = "https://nt-public-dev.example.test";

    /// <summary>The <c>Catalog:PublicCacheSeconds</c> default the cache test expects (plan §3).</summary>
    private const string ExpectedCacheControl = "public, max-age=3600";

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static string PublicBaseUrlKey =>
        $"{StorageOptions.SectionName}:{nameof(StorageOptions.PublicBaseUrl)}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_Only_Active_Voices()
    {
        var userId = Guid.CreateVersion7();
        var activeId = NewVoiceId();
        var inactiveId = NewVoiceId();
        await SeedVoiceAsync(activeId, displayName: "Thiện Minh (09.4.2-T1)");
        await SeedVoiceAsync(inactiveId, active: false);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var items = await ReadItemsAsync(response);
        items.Select(item => item.Id).ShouldBe(
            await QueryStringsAsync("SELECT id FROM voices WHERE active ORDER BY id"),
            "the answer is exactly the active catalog, ordered by id");
        items.ShouldNotContain(item => item.Id == inactiveId, "a retired voice is hidden");

        // The wire shape of a row (09 README D14): camelCase members, nothing else.
        AssertItemShape(
            await response.Content.ReadAsStringAsync(Ct),
            "id",
            "displayName",
            "language",
            "gender",
            "tags",
            "previewUrl");
    }

    [Fact]
    public async Task Filter_Language_Vi_Returns_Only_Vi()
    {
        var userId = Guid.CreateVersion7();
        var viId = NewVoiceId();
        var enId = NewVoiceId();
        await SeedVoiceAsync(viId, language: Language.Vi);
        await SeedVoiceAsync(enId, language: Language.En);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{Route}?language=vi", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var items = await ReadItemsAsync(response);
        items.ShouldAllBe(item => item.Language == "vi");
        items.Select(item => item.Id).ShouldBe(
            await QueryStringsAsync("SELECT id FROM voices WHERE active AND language = 'vi' ORDER BY id"));
        items.ShouldNotContain(item => item.Id == enId);
    }

    [Fact]
    public async Task Filter_Language_En_Returns_Only_En()
    {
        var userId = Guid.CreateVersion7();
        var viId = NewVoiceId();
        var enId = NewVoiceId();
        await SeedVoiceAsync(viId, language: Language.Vi);
        await SeedVoiceAsync(enId, language: Language.En, gender: VoiceGender.Male);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{Route}?language=en", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var items = await ReadItemsAsync(response);
        items.ShouldAllBe(item => item.Language == "en");
        items.Select(item => item.Id).ShouldBe(
            await QueryStringsAsync("SELECT id FROM voices WHERE active AND language = 'en' ORDER BY id"));
        items.ShouldNotContain(item => item.Id == viId);

        var seeded = items.Single(item => item.Id == enId);
        seeded.Gender.ShouldBe("male", "the gender travels snake_case (09 README D14)");
    }

    [Fact]
    public async Task No_Language_Filter_Returns_Both_Languages()
    {
        var userId = Guid.CreateVersion7();
        var viId = NewVoiceId();
        var enId = NewVoiceId();
        await SeedVoiceAsync(viId, language: Language.Vi);
        await SeedVoiceAsync(enId, language: Language.En);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var items = await ReadItemsAsync(response);
        items.ShouldContain(item => item.Id == viId, "an omitted filter is not 'vi only'");
        items.ShouldContain(item => item.Id == enId, "an omitted filter is not 'en only'");
        items.Select(item => item.Language).Distinct().ShouldContain("vi");
        items.Select(item => item.Language).Distinct().ShouldContain("en");
    }

    [Fact]
    public async Task Empty_Language_Filter_Over_Http_Returns_400_ValidationFailed()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(Guid.CreateVersion7());

        // `?language=` is a value the client sent (plan §Failure handling): the route must refuse it
        // under `errors.language`, not silently treat it as "no filter". The in-process sender pins the
        // same contract in CatalogQueryContractTests; this one checks the binder really delivers "".
        using var response = await client.GetAsync($"{Route}?language=", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe(ErrorCodes.ValidationFailed);
        problem.Errors!.ShouldContainKey("language");
    }

    [Fact]
    public async Task Unknown_Language_Returns_400_ValidationFailed_Field_Language()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var unknown = await client.GetAsync($"{Route}?language=xx", Ct);
        using var wrongCase = await client.GetAsync($"{Route}?language=VI", Ct);

        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var unknownProblem = await ReadProblemAsync(unknown);
        unknownProblem.Code.ShouldBe(ErrorCodes.ValidationFailed);
        unknownProblem.Errors!.ShouldContainKey("language");

        wrongCase.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the accepted spelling is ordinal");
        var wrongCaseProblem = await ReadProblemAsync(wrongCase);
        wrongCaseProblem.Code.ShouldBe(ErrorCodes.ValidationFailed);
        wrongCaseProblem.Errors!.ShouldContainKey("language");
    }

    [Fact]
    public async Task PreviewUrl_Uses_Public_Base_Url_And_Key()
    {
        var userId = Guid.CreateVersion7();
        var voiceId = NewVoiceId();
        const string Key = "voice-previews/nts-voices-t1.v1.mp3";
        await SeedVoiceAsync(voiceId, previewStorageKey: Key);

        using var host = new ApiFactory(Database, new FakeClock(Start))
            .WithWebHostBuilder(builder => builder.UseSetting(PublicBaseUrlKey, PublicBaseUrl));
        using var client = TestClient(host.CreateClient(), userId);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await ReadItemsAsync(response)).Single(item => item.Id == voiceId).PreviewUrl
            .ShouldBe($"{PublicBaseUrl}/{Key}");

        // A catalog URL is public and unsigned: a presigned URL belongs to 09.7.2-T1 (BD10/WF16), so no
        // signature can appear here.
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("X-Amz-Signature", Case.Sensitive, "a catalog preview is never presigned (BD10)");
        body.ShouldNotContain("Signature=", Case.Sensitive, "a catalog preview is never presigned (BD10)");
    }

    [Fact]
    public async Task PreviewUrl_Is_Null_When_Key_Missing()
    {
        var userId = Guid.CreateVersion7();
        var voiceId = NewVoiceId();
        await SeedVoiceAsync(voiceId);

        using var host = new ApiFactory(Database, new FakeClock(Start))
            .WithWebHostBuilder(builder => builder.UseSetting(PublicBaseUrlKey, PublicBaseUrl));
        using var client = TestClient(host.CreateClient(), userId);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await ReadItemsAsync(response)).Single(item => item.Id == voiceId).PreviewUrl.ShouldBeNull();
    }

    [Fact]
    public async Task PreviewUrl_Is_Null_When_Base_Url_Empty()
    {
        var userId = Guid.CreateVersion7();
        var voiceId = NewVoiceId();
        await SeedVoiceAsync(voiceId, previewStorageKey: "voice-previews/nts-voices-t1.v1.mp3");

        // The plain host: `Storage:PublicBaseUrl` is empty (its committed default), so the host cannot
        // compose a URL and the picker hides the play control.
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await ReadItemsAsync(response)).Single(item => item.Id == voiceId).PreviewUrl.ShouldBeNull();
    }

    [Fact]
    public async Task PreviewUrl_Is_Null_When_Key_Is_Absolute_Or_Contains_DotDot()
    {
        var userId = Guid.CreateVersion7();
        var absoluteId = NewVoiceId();
        var dotDotId = NewVoiceId();
        var rootRelativeId = NewVoiceId();
        await SeedVoiceAsync(absoluteId, previewStorageKey: "https://evil.example.test/voice-previews/x.mp3");
        await SeedVoiceAsync(dotDotId, previewStorageKey: "../voice-previews/x.mp3");
        await SeedVoiceAsync(rootRelativeId, previewStorageKey: "/voice-previews/x.mp3");

        using var host = new ApiFactory(Database, new FakeClock(Start))
            .WithWebHostBuilder(builder => builder.UseSetting(PublicBaseUrlKey, PublicBaseUrl));
        using var client = TestClient(host.CreateClient(), userId);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var body = await response.Content.ReadAsStringAsync(Ct);
        var items = await ReadItemsAsync(response);
        items.Single(item => item.Id == absoluteId).PreviewUrl.ShouldBeNull();
        items.Single(item => item.Id == dotDotId).PreviewUrl.ShouldBeNull();
        items.Single(item => item.Id == rootRelativeId).PreviewUrl.ShouldBeNull();

        // A refused key is never echoed — not even the host it claimed.
        body.ShouldNotContain("evil.example.test");
        body.ShouldNotContain("voice-previews/x.mp3");
    }

    [Fact]
    public async Task CacheControl_Is_Public_MaxAge()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.GetValues("Cache-Control").Single().ShouldBe(ExpectedCacheControl);
        response.Headers.ETag.ShouldNotBeNull();
    }

    [Fact]
    public async Task IfNoneMatch_Same_Etag_Returns_304_Without_Body()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var first = await client.GetAsync(Route, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        var etag = first.Headers.ETag.ShouldNotBeNull().ToString();

        using var revalidation = await SendAsync(client, "If-None-Match", etag);

        revalidation.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        revalidation.Headers.ETag.ShouldNotBeNull().ToString().ShouldBe(etag);
        revalidation.Headers.GetValues("Cache-Control").Single().ShouldBe(ExpectedCacheControl);
        (await revalidation.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty("a 304 carries no body");

        // `*` is not "any tag" on this route: the strong comparison of D7 matches the exact value only.
        using var star = await SendAsync(client, "If-None-Match", "*");

        star.StatusCode.ShouldBe(HttpStatusCode.OK, "a wildcard is not a hit (09.4.2 plan §Failure handling)");
    }

    [Fact]
    public async Task Catalog_Change_Changes_Etag_And_Answers200()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var first = await client.GetAsync(Route, Ct);
        var etag = first.Headers.ETag.ShouldNotBeNull().ToString();

        // A re-seed (04.2.2-T2) adds a voice: the tag the client holds is no longer current.
        await SeedVoiceAsync(NewVoiceId(), displayName: "Giọng mới (09.4.2-T1)");

        using var revalidation = await SendAsync(client, "If-None-Match", etag);

        revalidation.StatusCode.ShouldBe(HttpStatusCode.OK);
        revalidation.Headers.ETag.ShouldNotBeNull().ToString().ShouldNotBe(etag);
        (await ReadItemsAsync(revalidation)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Requires_Authentication()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>A voice id unique to one test: the class database is shared by every test of the class.</summary>
    private static string NewVoiceId() => $"nts-v-{Guid.CreateVersion7():N}";

    /// <summary>The authenticated client of a host whose configuration a test changed.</summary>
    private static HttpClient TestClient(HttpClient client, Guid userId)
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());

        return client;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string header, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.TryAddWithoutValidation(header, value);

        return await client.SendAsync(request, Ct);
    }

    private static async Task<List<VoiceItem>> ReadItemsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<CatalogBody<VoiceItem>>(Ct);
        body.ShouldNotBeNull();

        return body.Items;
    }

    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)response.StatusCode);

        return problem;
    }

    /// <summary>Asserts the members of the first item of a catalog body, and nothing more (09 README D14).</summary>
    private static void AssertItemShape(string json, params string[] members)
    {
        using var document = JsonDocument.Parse(json);
        var first = document.RootElement.GetProperty("items")[0];

        first.EnumerateObject().Select(property => property.Name).ShouldBe(members, ignoreOrder: true);
    }

    private async Task SeedVoiceAsync(
        string id,
        string? displayName = null,
        Language language = Language.Vi,
        VoiceGender gender = VoiceGender.Female,
        string? previewStorageKey = null,
        bool active = true)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser());
        context.Voices.Add(Voice.Create(
            id,
            displayName ?? id,
            language,
            gender,
            ["narrator"],
            previewStorageKey,
            active));
        await context.SaveChangesAsync(Ct);
    }

    private async Task<IReadOnlyList<string>> QueryStringsAsync(string sql) =>
        await Database.QueryStringsAsync(sql, Ct);

    /// <summary>One row of the catalog route.</summary>
    private sealed record VoiceItem(
        string Id,
        string DisplayName,
        string Language,
        string Gender,
        List<string> Tags,
        string? PreviewUrl);

    /// <summary>The <c>{ items }</c> envelope of a catalog route (plan §5).</summary>
    private sealed record CatalogBody<T>(List<T> Items);

    /// <summary>The ProblemDetails members these tests assert on (09 README D7).</summary>
    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
