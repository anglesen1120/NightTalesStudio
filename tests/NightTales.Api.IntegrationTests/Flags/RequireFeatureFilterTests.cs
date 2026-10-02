using System.Net;
using System.Net.Http.Json;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Application.Common.Errors;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Flags;

/// <summary>
/// The route guard of 09 README BD13 over the real host: a disabled feature answers 404
/// <c>feature_disabled</c> with a trace id and an unexecuted handler, an enabled one runs the handler, and
/// an anonymous caller gets the same 404 rather than a 401 — never a 403 (no existence leak).
/// </summary>
/// <remarks>
/// The probe route is mapped through <c>ApiFactory.MapEndpoints</c>, so the filter runs in the production
/// pipeline: the same ProblemDetails writer, the same fallback policy and the same cached
/// <c>IFeatureFlags</c> the consumer routes will resolve.
/// </remarks>
public sealed class RequireFeatureFilterTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Flag_off_returns_404_feature_disabled_with_traceId()
    {
        FlagsProbeEndpoints.Reset();
        await SetFlagAsync(enabled: false);

        using var factory = BuildFactory();
        using var client = factory.AnonymousClient();

        var response = await client.GetAsync(FlagsProbeEndpoints.GuardedPath, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem.ShouldNotBeNull();
        problem!.Code.ShouldBe(ErrorCodes.FeatureDisabled);
        problem.Status.ShouldBe((int)HttpStatusCode.NotFound);
        problem.Type.ShouldBe($"https://nighttales.app/errors/{ErrorCodes.FeatureDisabled}");
        problem.Title.ShouldNotBeNullOrWhiteSpace();
        problem.TraceId.ShouldNotBeNullOrWhiteSpace("the body carries the request's trace id (D7)");
    }

    [Fact]
    public async Task Flag_off_does_not_invoke_handler()
    {
        FlagsProbeEndpoints.Reset();
        await SetFlagAsync(enabled: false);

        using var factory = BuildFactory();
        using var client = factory.AnonymousClient();

        (await client.GetAsync(FlagsProbeEndpoints.GuardedPath, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);

        FlagsProbeEndpoints.Invocations.ShouldBe(0, "the guard answers before the handler runs");
    }

    [Fact]
    public async Task Flag_on_invokes_handler()
    {
        FlagsProbeEndpoints.Reset();
        await SetFlagAsync(enabled: true);

        using var factory = BuildFactory();
        using var client = factory.AnonymousClient();

        var response = await client.GetAsync(FlagsProbeEndpoints.GuardedPath, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe(FlagsProbeEndpoints.HandlerBody);
        FlagsProbeEndpoints.Invocations.ShouldBe(1);
    }

    /// <summary>
    /// A disabled feature is 404 for everyone, including a caller who has not authenticated: the guard's
    /// answer is the route's answer, and a 401 or a 403 would leak that something is there.
    /// </summary>
    [Fact]
    public async Task Guarded_route_without_auth_returns_404_not_401()
    {
        FlagsProbeEndpoints.Reset();
        await SetFlagAsync(enabled: false);

        using var factory = BuildFactory();
        using var client = factory.AnonymousClient();

        var response = await client.GetAsync(FlagsProbeEndpoints.GuardedPath, Ct);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe(ErrorCodes.FeatureDisabled);
        FlagsProbeEndpoints.Invocations.ShouldBe(0);
    }

    private ApiFactory BuildFactory()
    {
        var factory = new ApiFactory(Database, new FakeClock());
        factory.MapEndpoints(FlagsProbeEndpoints.Map);

        return factory;
    }

    /// <summary>Writes the guarded flag's state before the host (and its cache) is built.</summary>
    private Task SetFlagAsync(bool enabled) =>
        Database.ExecuteAsync(
            "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
            + $"VALUES ('{FlagsProbeEndpoints.GuardedKey}', {(enabled ? "true" : "false")}, '{{}}', now(), now()) "
            + "ON CONFLICT (key) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = now()",
            Ct);
}
