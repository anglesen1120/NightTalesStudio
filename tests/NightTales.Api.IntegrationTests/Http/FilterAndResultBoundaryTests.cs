using System.Net;
using System.Net.Http.Json;
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
/// QA boundary cases of the endpoint filters and the result helpers (09.1.4-T1), through the real
/// host: <c>Idempotency-Key</c> byte boundaries, <c>If-Match</c> shapes and the success-result
/// helpers <c>ToAcceptedResult</c>, <c>ToNoContentResult</c> and <c>ToHttpResult</c>.
/// </summary>
public sealed class FilterAndResultBoundaryTests : IntegrationTest
{
    public FilterAndResultBoundaryTests(TestDatabase database)
        : base(database)
    {
    }

    [Theory]
    [InlineData("\t")]
    [InlineData(" ")]
    [InlineData("\u007F")]
    [InlineData("\u0080")]
    [InlineData("çağrı")]
    [InlineData("line1\nline2")]
    public async Task RequireIdempotencyKey_InvalidShapes_Returns400(string key)
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await PostWithKeyAsync(client, key);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, TestContextToken.Value);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe("idempotency_key_required");
    }

    [Fact]
    public async Task RequireIdempotencyKey_OneCharAnd128Chars_AreAccepted()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var single = await PostWithKeyAsync(client, "x");
        single.StatusCode.ShouldBe(HttpStatusCode.OK, "one printable char is the shortest valid key");

        using var max = await PostWithKeyAsync(client, new string('k', RequireIdempotencyKeyFilter.MaxLength));
        max.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RequireIdempotencyKey_DuplicatedHeader_Returns400()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var request = new HttpRequestMessage(HttpMethod.Post, ProbeEndpoints.IdempotencyPath);
        request.Headers.TryAddWithoutValidation(RequireIdempotencyKeyFilter.HeaderName, "first");
        request.Headers.TryAddWithoutValidation(RequireIdempotencyKeyFilter.HeaderName, "second");

        using var response = await client.SendAsync(request, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            "two keys on one request are ambiguous; the filter requires exactly one");
        var body = await response.Content.ReadAsStringAsync(TestContextToken.Value);
        body.ShouldNotContain("first", Case.Sensitive, "key material is never echoed");
        body.ShouldNotContain("second", Case.Sensitive);
    }

    [Fact]
    public async Task RequireIfMatch_WhitespaceHeader_Returns428()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var whitespace = new HttpRequestMessage(HttpMethod.Post, ProbeEndpoints.IfMatchPath);
        whitespace.Headers.TryAddWithoutValidation(RequireIfMatchFilter.HeaderName, "   ");
        using var whitespaceResponse = await client.SendAsync(whitespace, TestContextToken.Value);
        whitespaceResponse.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired,
            "a whitespace If-Match carries no tag the handler could compare");
    }

    [Fact]
    public async Task RequireIfMatch_DuplicatedHeader_PassesAsJoinedValue()
    {
        // The filter requires presence only. Two headers reach the binder as one joined value that
        // ETags.Matches then rejects (a comma-separated list never equals the one tag), so the
        // handler still answers 412.
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var duplicated = new HttpRequestMessage(HttpMethod.Post, ProbeEndpoints.IfMatchPath);
        duplicated.Headers.TryAddWithoutValidation(RequireIfMatchFilter.HeaderName, "\"ep-1\"");
        duplicated.Headers.TryAddWithoutValidation(RequireIfMatchFilter.HeaderName, "\"ep-2\"");
        using var duplicatedResponse = await client.SendAsync(duplicated, TestContextToken.Value);

        duplicatedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var probe = await duplicatedResponse.Content.ReadFromJsonAsync<ProbeEndpoints.IfMatchProbe>(TestContextToken.Value);
        probe.ShouldNotBeNull();
        probe.IfMatch.ShouldNotBeNull();
        probe.IfMatch.ShouldContain("\"ep-1\"");
        probe.IfMatch.ShouldContain("\"ep-2\"");
        NightTales.Application.Common.ETags.Matches(
            probe.IfMatch, NightTales.Application.Common.ETags.Episode(1)).ShouldBeFalse(
            "the joined value never matches the single tag the handler compares against");
    }

    [Fact]
    public async Task RequireIfMatch_Star_IsAcceptedByTheFilter_ForTheHandlerToReject()
    {
        // The filter only requires presence; ETags.Matches refuses "*", so a client sending it still
        // gets 412 from the handler (task edge table), not a free pass at the endpoint.
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var request = new HttpRequestMessage(HttpMethod.Post, ProbeEndpoints.IfMatchPath);
        request.Headers.TryAddWithoutValidation(RequireIfMatchFilter.HeaderName, "*");
        using var response = await client.SendAsync(request, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the filter passes any present value through to the handler");
        var probe = await response.Content.ReadFromJsonAsync<ProbeEndpoints.IfMatchProbe>(TestContextToken.Value);
        probe.ShouldNotBeNull();
        probe.IfMatch.ShouldBe("*");
    }

    [Fact]
    public async Task ToAcceptedResult_SetsLocationAndValueETag()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var withLocation = await client.PostAsync(ProbeEndpoints.AcceptedPath, content: null, TestContextToken.Value);
        withLocation.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        withLocation.Headers.Location.ShouldNotBeNull();
        withLocation.Headers.Location!.ToString().ShouldBe($"{ProbeEndpoints.AcceptedPath}/9");
        withLocation.Headers.ETag.ShouldNotBeNull("the value implements IHasETag");
        withLocation.Headers.ETag!.Tag.ShouldBe("\"ep-4\"");

        using var withoutLocation = await client.PostAsync(ProbeEndpoints.AcceptedNoLocationPath, content: null, TestContextToken.Value);
        withoutLocation.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        withoutLocation.Headers.Location.ShouldBeNull("no location delegate means no Location header");
    }

    [Fact]
    public async Task ToNoContentResult_Returns204AndKeepsETag()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var response = await client.PostAsync(ProbeEndpoints.NoContentPath, content: null, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var body = await response.Content.ReadAsStringAsync(TestContextToken.Value);
        body.ShouldBeEmpty("204 carries no body");
        response.Headers.ETag.ShouldNotBeNull("IHasETag values still set ETag on an empty response");
        response.Headers.ETag!.Tag.ShouldBe("\"ep-6\"");
    }

    [Fact]
    public async Task ToHttpResult_SetsETagFromTheValue_AndSkipsEmptyETag()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(Guid.CreateVersion7());

        using var tagged = await client.GetAsync(ProbeEndpoints.HttpResultPath, TestContextToken.Value);
        tagged.StatusCode.ShouldBe(HttpStatusCode.OK);
        tagged.Headers.ETag.ShouldNotBeNull();
        tagged.Headers.ETag!.Tag.ShouldBe("\"ep-8\"");

        using var empty = await client.GetAsync(ProbeEndpoints.HttpResultEmptyETagPath, TestContextToken.Value);
        empty.StatusCode.ShouldBe(HttpStatusCode.OK);
        empty.Headers.ETag.ShouldBeNull("an empty IHasETag value must not write an ETag header");
    }

    private static async Task<HttpResponseMessage> PostWithKeyAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ProbeEndpoints.IdempotencyPath);
        request.Headers.TryAddWithoutValidation(RequireIdempotencyKeyFilter.HeaderName, key);

        return await client.SendAsync(request, TestContextToken.Value);
    }
}
