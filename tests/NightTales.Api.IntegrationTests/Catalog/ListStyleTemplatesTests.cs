using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Catalog;
using NightTales.Domain.Series;
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
/// <c>GET /api/v1/style-templates</c> against the real host and a real PostgreSQL 17 (09.4.2 plan §9):
/// the true-crime rule of 03 CR-2, the active filter and the item shape.
/// </summary>
/// <remarks>
/// <para>
/// The topic expectation of each test is written as SQL over <c>style_templates</c> rather than repeated
/// in C#: that keeps the 03 CR-2 rule in the single place that owns it
/// (<see cref="StyleTopicPolicy"/>) while still checking the route against the rows that are really
/// there. The class database is shared by every test of this class and a Development host seeds the
/// 03 catalog on start (09.2.5), so no test asserts a hard-coded total.
/// </para>
/// <para>
/// A catalog route is not user-owned (D10 filters user rows only), so a test needs no <c>users</c> row —
/// only an authenticated caller, which the test authentication scheme provides from the user id.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ListStyleTemplatesTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Route = "/api/v1/style-templates";

    /// <summary>The public bucket base URL the preview test configures the host with.</summary>
    private const string PublicBaseUrl = "https://nt-public-dev.example.test";

    /// <summary>
    /// The template text every seeded row uses: one <c>{term}</c>, 60–600 characters, no line break, no
    /// double quote and none of the words 03.1.1-T2 reserves for the negative clause.
    /// </summary>
    private const string TemplateText =
        "Một khung cảnh {term} được kể bằng nét vẽ đơn sắc trên nền giấy cũ, ánh sáng dịu và bố cục rộng.";

    /// <summary>The <c>Catalog:PublicCacheSeconds</c> default the cache tests expect (plan §3).</summary>
    private const string ExpectedCacheControl = "public, max-age=3600";

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static string PublicBaseUrlKey =>
        $"{StorageOptions.SectionName}:{nameof(StorageOptions.PublicBaseUrl)}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Topic_TrueCrime_Returns_Only_TrueCrime_Templates()
    {
        var userId = Guid.CreateVersion7();
        var trueCrimeId = await SeedTemplateAsync(Topic.TrueCrime);
        var ghostId = await SeedTemplateAsync(Topic.Ghost);
        var anyTopicId = await SeedTemplateAsync(topicHint: null);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{Route}?topic=true_crime", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var items = await ReadItemsAsync(response);
        items.ShouldAllBe(item => item.TopicHint == "true_crime");
        items.Select(item => item.Id.ToString()).ShouldBe(
            await QueryStringsAsync(
                "SELECT id::text FROM style_templates WHERE active AND topic_hint = 'true_crime' ORDER BY id"));
        items.ShouldNotContain(item => item.Id == ghostId);
        items.ShouldNotContain(item => item.Id == anyTopicId);
        items.ShouldContain(item => item.Id == trueCrimeId);
    }

    [Fact]
    public async Task Topic_Ghost_Excludes_TrueCrime_Templates()
    {
        var userId = Guid.CreateVersion7();
        var trueCrimeId = await SeedTemplateAsync(Topic.TrueCrime);
        var ghostId = await SeedTemplateAsync(Topic.Ghost);
        var anyTopicId = await SeedTemplateAsync(topicHint: null);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{Route}?topic=ghost", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var items = await ReadItemsAsync(response);
        items.ShouldNotContain(item => item.TopicHint == "true_crime");
        items.Select(item => item.Id.ToString()).ShouldBe(
            await QueryStringsAsync(
                "SELECT id::text FROM style_templates WHERE active AND (topic_hint IS NULL OR topic_hint <> 'true_crime') ORDER BY id"));
        items.ShouldNotContain(item => item.Id == trueCrimeId);
        items.ShouldContain(item => item.Id == ghostId);
        items.ShouldContain(item => item.Id == anyTopicId);
    }

    [Fact]
    public async Task Topic_Spiritual_Excludes_TrueCrime_Templates()
    {
        var userId = Guid.CreateVersion7();
        var trueCrimeId = await SeedTemplateAsync(Topic.TrueCrime);
        var spiritualId = await SeedTemplateAsync(Topic.Spiritual);
        var anyTopicId = await SeedTemplateAsync(topicHint: null);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{Route}?topic=spiritual", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var items = await ReadItemsAsync(response);
        items.ShouldNotContain(item => item.TopicHint == "true_crime");
        items.Select(item => item.Id.ToString()).ShouldBe(
            await QueryStringsAsync(
                "SELECT id::text FROM style_templates WHERE active AND (topic_hint IS NULL OR topic_hint <> 'true_crime') ORDER BY id"));
        items.ShouldNotContain(item => item.Id == trueCrimeId);
        items.ShouldContain(item => item.Id == spiritualId);
        items.ShouldContain(item => item.Id == anyTopicId);
    }

    [Fact]
    public async Task No_Topic_Returns_All_Active_Templates()
    {
        var userId = Guid.CreateVersion7();
        var trueCrimeId = await SeedTemplateAsync(Topic.TrueCrime);
        var ghostId = await SeedTemplateAsync(Topic.Ghost);
        var inactiveId = await SeedTemplateAsync(Topic.TrueCrime, active: false);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var items = await ReadItemsAsync(response);
        items.Select(item => item.Id.ToString()).ShouldBe(
            await QueryStringsAsync("SELECT id::text FROM style_templates WHERE active ORDER BY id"));
        items.ShouldContain(item => item.Id == trueCrimeId, "an omitted topic is 'no filter', not 'fiction only'");
        items.ShouldContain(item => item.Id == ghostId);
        items.ShouldNotContain(item => item.Id == inactiveId);
    }

    [Fact]
    public async Task Inactive_Template_Is_Hidden()
    {
        var userId = Guid.CreateVersion7();
        var inactiveId = await SeedTemplateAsync(topicHint: null, active: false);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var withoutTopic = await client.GetAsync(Route, Ct);
        using var withTopic = await client.GetAsync($"{Route}?topic=ghost", Ct);

        withoutTopic.StatusCode.ShouldBe(HttpStatusCode.OK, await withoutTopic.Content.ReadAsStringAsync(Ct));
        withTopic.StatusCode.ShouldBe(HttpStatusCode.OK, await withTopic.Content.ReadAsStringAsync(Ct));

        (await ReadItemsAsync(withoutTopic)).ShouldNotContain(
            item => item.Id == inactiveId,
            "a de-activated template disappears from the catalog (03 README L98)");
        (await ReadItemsAsync(withTopic)).ShouldNotContain(
            item => item.Id == inactiveId,
            "the topic filter never resurrects an inactive row");
    }

    [Fact]
    public async Task Empty_Topic_Filter_Over_Http_Returns_400_ValidationFailed()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(Guid.CreateVersion7());

        // `?topic=` is a value the client sent (plan §Failure handling): the route must refuse it under
        // `errors.topic`, not silently treat it as "no filter". The in-process sender pins the same
        // contract in CatalogQueryContractTests; this one checks the binder really delivers "".
        using var response = await client.GetAsync($"{Route}?topic=", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe(ErrorCodes.ValidationFailed);
        problem.Errors!.ShouldContainKey("topic");
    }

    [Fact]
    public async Task Unknown_Topic_Returns_400_ValidationFailed_Field_Topic()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var unknown = await client.GetAsync($"{Route}?topic=news", Ct);
        using var wrongCase = await client.GetAsync($"{Route}?topic=TRUE_CRIME", Ct);

        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var unknownProblem = await ReadProblemAsync(unknown);
        unknownProblem.Code.ShouldBe(ErrorCodes.ValidationFailed);
        unknownProblem.Errors!.ShouldContainKey("topic");

        wrongCase.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the accepted spelling is ordinal snake_case");
        var wrongCaseProblem = await ReadProblemAsync(wrongCase);
        wrongCaseProblem.Code.ShouldBe(ErrorCodes.ValidationFailed);
        wrongCaseProblem.Errors!.ShouldContainKey("topic");
    }

    [Fact]
    public async Task Item_Shape_Is_Id_Name_TopicHint_PreviewUrl()
    {
        var userId = Guid.CreateVersion7();
        const string Key = "style-previews/nts-t1.v1.webp";
        var name = NewTemplateName();
        var templateId = await SeedTemplateAsync(Topic.TrueCrime, previewStorageKey: Key, name: name);

        using var host = new ApiFactory(Database, new FakeClock(Start))
            .WithWebHostBuilder(builder => builder.UseSetting(PublicBaseUrlKey, PublicBaseUrl));
        using var client = TestClient(host.CreateClient(), userId);

        using var response = await client.GetAsync($"{Route}?topic=true_crime", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var body = await response.Content.ReadAsStringAsync(Ct);
        using (var document = JsonDocument.Parse(body))
        {
            var first = document.RootElement.GetProperty("items")[0];
            first.EnumerateObject().Select(property => property.Name)
                .ShouldBe(["id", "name", "topicHint", "previewUrl"], ignoreOrder: true);
        }

        var item = (await ReadItemsAsync(response)).Single(candidate => candidate.Id == templateId);
        item.Name.ShouldBe(name);
        item.TopicHint.ShouldBe("true_crime", "the topic hint travels snake_case (09 README D14)");
        item.PreviewUrl.ShouldBe($"{PublicBaseUrl}/{Key}");
    }

    [Fact]
    public async Task CacheControl_And_Etag_Are_Public()
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
        var userId = Guid.CreateVersion7();
        var templateName = NewTemplateName();
        await SeedTemplateAsync(topicHint: null, name: templateName);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var first = await client.GetAsync(Route, Ct);
        var etag = first.Headers.ETag.ShouldNotBeNull().ToString();

        // A re-seed (03.1.1-T1) de-activates a template: the tag the client holds is no longer current.
        await DeactivateTemplateAsync(templateName);

        using var revalidation = await SendAsync(client, "If-None-Match", etag);

        revalidation.StatusCode.ShouldBe(HttpStatusCode.OK);
        revalidation.Headers.ETag.ShouldNotBeNull().ToString().ShouldNotBe(etag);
        (await ReadItemsAsync(revalidation)).ShouldNotContain(item => item.Name == templateName);
    }

    [Fact]
    public async Task Requires_Authentication()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>A template name unique to one test: the class database is shared by every test of the class.</summary>
    private static string NewTemplateName() => $"nts-t1-{Guid.CreateVersion7():N}";

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string header, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.TryAddWithoutValidation(header, value);

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The authenticated client of a host whose configuration a test changed.</summary>
    private static HttpClient TestClient(HttpClient client, Guid userId)
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());

        return client;
    }

    private static async Task<List<StyleTemplateItem>> ReadItemsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<CatalogBody<StyleTemplateItem>>(Ct);
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

    private async Task<Guid> SeedTemplateAsync(
        Topic? topicHint,
        bool active = true,
        string? previewStorageKey = null,
        string? name = null)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser());
        var template = StyleTemplate.Create(
            name ?? NewTemplateName(),
            TemplateText,
            topicHint,
            previewStorageKey,
            active);
        context.StyleTemplates.Add(template);
        await context.SaveChangesAsync(Ct);

        return template.Id;
    }

    private async Task<IReadOnlyList<string>> QueryStringsAsync(string sql) =>
        await Database.QueryStringsAsync(sql, Ct);

    /// <summary>Flips a template inactive, the way the 03 seed update of a retired style would.</summary>
    private async Task DeactivateTemplateAsync(string name)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser());
        var template = await context.StyleTemplates.SingleAsync(candidate => candidate.Name == name, Ct);
        template.UpdateFromCatalog(
            template.Template,
            template.TopicHint,
            template.PreviewStorageKey,
            active: false);
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>One row of the catalog route.</summary>
    private sealed record StyleTemplateItem(
        Guid Id,
        string Name,
        string? TopicHint,
        string? PreviewUrl);

    /// <summary>The <c>{ items }</c> envelope of a catalog route (plan §5).</summary>
    private sealed record CatalogBody<T>(List<T> Items);

    /// <summary>The ProblemDetails members these tests assert on (09 README D7).</summary>
    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
