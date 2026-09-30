using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit.Database;
using NightTales.TestKit.Health;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The Worker's readiness contract (09.2.6-T1 acceptance, 09.9-D2) against an unreachable database:
/// liveness stays 200, readiness is the only 503, and the body carries no credential.
/// </summary>
/// <remarks>
/// These tests need no PostgreSQL: the Worker health pipeline is pointed at a closed port, which is
/// exactly the condition the acceptance check describes (never stop the shared dev server).
/// </remarks>
public sealed class WorkerHealthTests
{
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=nts_unreachable;Username=nts;Password=nts-not-a-real-password";

    [Fact]
    public async Task WorkerHealth_LiveIsOk_WhenDatabaseIsUnreachable()
    {
        await using var probe = await WorkerHealthProbe.StartAsync(
            UnreachableDatabase,
            cancellationToken: TestContextToken.Value);

        using var client = probe.CreateClient();
        using var response = await client.GetAsync(WorkerHealthProbe.LivePath, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WorkerHealth_ReadyIs503_WhenDatabaseIsUnreachable()
    {
        await using var probe = await WorkerHealthProbe.StartAsync(
            UnreachableDatabase,
            cancellationToken: TestContextToken.Value);

        using var client = probe.CreateClient();
        using var response = await client.GetAsync(WorkerHealthProbe.ReadyPath, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var body = await response.Content.ReadAsStringAsync(TestContextToken.Value);
        body.ShouldNotContain("nts-not-a-real-password");

        var report = JsonDocument.Parse(body).RootElement;
        report.GetProperty("status").GetString().ShouldBe("unhealthy");
        report.GetProperty("checks")
            .EnumerateArray()
            .ShouldContain(check => check.GetProperty("name").GetString() == "database"
                && check.GetProperty("status").GetString() == "unhealthy");
    }

    [Fact]
    public async Task WorkerHealth_ReadyBody_NeverCarriesTheConnectionString()
    {
        // The probe is unauthenticated: a failing check reports a constant, never the exception.
        await using var probe = await WorkerHealthProbe.StartAsync(
            UnreachableDatabase,
            cancellationToken: TestContextToken.Value);

        using var client = probe.CreateClient();
        using var response = await client.GetAsync(WorkerHealthProbe.ReadyPath, TestContextToken.Value);

        var report = await response.Content.ReadFromJsonAsync<JsonElement>(TestContextToken.Value);
        var database = report.GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "database");

        database.GetProperty("description").GetString().ShouldBe("database unreachable");
    }

    [Fact]
    public async Task WorkerHealthProbe_NonLoopbackServer_IsRefused()
    {
        // The probe runs the production graph, which installs Hangfire's schema, so its target passes
        // the fixture's destructive-DDL boundary before anything connects (SEC 09.2.6-T1/SEC-01).
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => WorkerHealthProbe.StartAsync(RemoteHostDatabase, cancellationToken: TestContextToken.Value));

        exception.Message.ShouldContain("not a loopback host");
    }

    [Fact]
    public async Task WorkerHealthProbe_UnmanagedDatabaseName_IsRefused()
    {
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => WorkerHealthProbe.StartAsync(UnmanagedDatabase, cancellationToken: TestContextToken.Value));

        exception.Message.ShouldContain(PostgresFixture.ManagedDatabasePrefix);
    }

    private const string RemoteHostDatabase =
        "Host=nts-remote.invalid;Port=5432;Database=nts_remote;Username=nts;Password=nts-not-a-real-password";

    private const string UnmanagedDatabase =
        "Host=127.0.0.1;Port=5432;Database=nighttales;Username=nts;Password=nts-not-a-real-password";
}
