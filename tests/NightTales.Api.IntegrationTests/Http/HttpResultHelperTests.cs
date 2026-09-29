using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// The endpoint half of 09.1.4-T1 through the real host: <c>ToCreatedResult</c>, the post-commit
/// <c>ToHttpResult</c> / <c>ToNoContentResult</c> ETag delegates, the two filters and the 500 shape of
/// an unhandled exception. The probe routes are mapped through <c>ApiFactory.MapEndpoints</c>, so
/// routing, the filters and the response writers all run for real.
/// </summary>
public sealed class HttpResultHelperTests : IntegrationTest
{
    public HttpResultHelperTests(TestDatabase database)
        : base(database)
    {
    }

    [Fact]
    public async Task ToCreatedResult_SetsLocationAndETag()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await client.PostAsync(ProbeEndpoints.CreatedPath, content: null, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe($"{ProbeEndpoints.CreatedPath}/7");
        response.Headers.ETag.ShouldNotBeNull("the etag delegate runs after the handler and sets the header");
        response.Headers.ETag!.Tag.ShouldBe(ProbeEndpoints.CreatedETag);

        var created = await response.Content.ReadFromJsonAsync<ProbeEndpoints.ProbeCreated>(TestContextToken.Value);
        created.ShouldNotBeNull();
        created.Id.ShouldBe(7);
    }

    [Fact]
    public async Task ToCreatedResult_FallsBackToTheValueETag()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await client.PostAsync(ProbeEndpoints.TaggedPath, content: null, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe($"{ProbeEndpoints.TaggedPath}/5");
        response.Headers.ETag.ShouldNotBeNull("the value implements IHasETag and the delegate added nothing");
        response.Headers.ETag!.Tag.ShouldBe("\"ep-3\"");
    }

    [Fact]
    public async Task ToHttpResult_WithETagDelegate_UsesTheDelegateValue()
    {
        // D15: the endpoint reads the ETag after commit through the delegate, and that value is what
        // the client sees — not the "ep-8" of the value it was handed.
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await client.GetAsync(ProbeEndpoints.HttpResultDelegatePath, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldBe(ProbeEndpoints.FreshETag);
        response.Headers.ETag.Tag.ShouldNotBe("\"ep-8\"", "the pre-commit value's own IHasETag is not the source");
    }

    [Fact]
    public async Task ToHttpResult_WithETagDelegateThatFindsNothing_IgnoresTheValueTag()
    {
        // The delegate is authoritative: the value was built before the commit, so its tag is stale
        // and must never reach an If-Match client.
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await client.GetAsync(ProbeEndpoints.HttpResultDelegateNullPath, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldBeNull(
            "a delegate that finds nothing must not fall back to the value's pre-commit ETag");
    }

    [Fact]
    public async Task ToNoContentResult_WithETagDelegate_UsesTheDelegateValue()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await client.PostAsync(ProbeEndpoints.NoContentDelegatePath, content: null, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldBe(ProbeEndpoints.FreshETag);
        response.Headers.ETag.Tag.ShouldNotBe("\"ep-6\"");
    }

    [Fact]
    public async Task ToNoContentResult_WithETagDelegateThatFindsNothing_IgnoresTheValueTag()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await client.PostAsync(
            ProbeEndpoints.NoContentDelegateNullPath, content: null, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.ETag.ShouldBeNull(
            "a delegate that finds nothing must not fall back to the value's pre-commit ETag");
    }

    [Fact]
    public async Task RequireIfMatch_Missing_Returns428()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var missing = await client.PostAsync(ProbeEndpoints.IfMatchPath, content: null, TestContextToken.Value);

        missing.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        var problem = await missing.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, TestContextToken.Value);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe("if_match_required");
        problem.Status.ShouldBe(428);
        problem.Type.ShouldBe("https://nighttales.app/errors/if_match_required");

        // Positive control: with the header the filter lets the request through and the binder sees it.
        using var request = new HttpRequestMessage(HttpMethod.Post, ProbeEndpoints.IfMatchPath);
        request.Headers.TryAddWithoutValidation(RequireIfMatchFilter.HeaderName, "\"ep-1\"");
        using var accepted = await client.SendAsync(request, TestContextToken.Value);

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var probe = await accepted.Content.ReadFromJsonAsync<ProbeEndpoints.IfMatchProbe>(TestContextToken.Value);
        probe.ShouldNotBeNull();
        probe.IfMatch.ShouldBe("\"ep-1\"");
    }

    [Fact]
    public async Task RequireIdempotencyKey_Missing_Returns400()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var missing = await client.PostAsync(ProbeEndpoints.IdempotencyPath, content: null, TestContextToken.Value);

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await missing.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, TestContextToken.Value);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe("idempotency_key_required");
        problem.Status.ShouldBe(400);
        problem.Type.ShouldBe("https://nighttales.app/errors/idempotency_key_required");

        // 129 characters: one over the column limit (09 CR-4).
        var tooLong = new string('k', RequireIdempotencyKeyFilter.MaxLength + 1);
        using var overlong = await PostWithKeyAsync(client, tooLong);
        overlong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var overlongBody = await overlong.Content.ReadAsStringAsync(TestContextToken.Value);
        overlongBody.ShouldNotContain(tooLong, Case.Sensitive, "the key is a client secret and is never echoed");

        // A space (0x20) is outside the printable range 0x21–0x7E.
        using var unprintable = await PostWithKeyAsync(client, "bad key");
        unprintable.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Positive control: exactly 128 printable characters pass and the handler runs.
        using var accepted = await PostWithKeyAsync(client, new string('k', RequireIdempotencyKeyFilter.MaxLength));
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var probe = await accepted.Content.ReadFromJsonAsync<ProbeEndpoints.IdempotencyProbe>(TestContextToken.Value);
        probe.ShouldNotBeNull();
        probe.Outcome.ShouldBe("accepted");
    }

    [Fact]
    public async Task UnhandledException_Returns500_WithoutExceptionText()
    {
        // Production: there is no developer exception page, so UseExceptionHandler answers through
        // IProblemDetailsService and the customisation in Program (09.1.4-T1 UC6, plan §8).
        await using var factory = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(
                    new MapEndpointsStartupFilter(ProbeEndpoints.Map)));
            });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(ProbeEndpoints.ThrowPath, TestContextToken.Value);

        await AssertInternalErrorWithoutExceptionTextAsync(response);
    }

    [Fact]
    public async Task UnhandledException_InDevelopment_AlsoHidesTheExceptionText()
    {
        // ApiFactory runs the host in Development (WebApplicationFactory), where Program's own
        // exception handler sits behind the developer exception page: the same customisation must
        // keep the exception text out of a JSON client's body there too.
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ProbeEndpoints.ThrowPath, TestContextToken.Value);

        await AssertInternalErrorWithoutExceptionTextAsync(response);
    }

    private static async Task<HttpResponseMessage> PostWithKeyAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ProbeEndpoints.IdempotencyPath);
        request.Headers.TryAddWithoutValidation(RequireIdempotencyKeyFilter.HeaderName, key);

        return await client.SendAsync(request, TestContextToken.Value);
    }

    private static async Task AssertInternalErrorWithoutExceptionTextAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType!.MediaType.ShouldBe(ProblemDetailsWriter.JsonContentType);

        var body = await response.Content.ReadAsStringAsync(TestContextToken.Value);
        body.ShouldNotContain(ProbeEndpoints.ThrowMessage, Case.Sensitive);
        body.ShouldNotContain("System.InvalidOperationException", Case.Sensitive);
        body.ShouldNotContain("Exception", Case.Sensitive);

        var problem = JsonSerializer.Deserialize<ProblemResponse>(body, ProblemResponse.Options);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe(500);
        problem.Code.ShouldBe("internal_error");
        problem.Type.ShouldBe("https://nighttales.app/errors/internal_error");
        problem.Title.ShouldBe(ProblemDetailsWriter.InternalErrorTitle);
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
    }
}
