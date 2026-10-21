using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Health;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The 09.9-D2 rule: a degraded Hangfire storage is reported in the body and never turns the
/// readiness probe into a 503 — only a failing database check does.
/// </summary>
public sealed class WorkerHealthDegradedTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task WorkerHealth_ReadyStays200_WhenOnlyHangfireIsDegraded()
    {
        await using var probe = await WorkerHealthProbe.StartAsync(
            Database.ConnectionString,
            services =>
            {
                services.RemoveAll<JobStorage>();
                services.AddSingleton<JobStorage>(new ThrowingJobStorage());
            },
            TestContextToken.Value);

        using var client = probe.CreateClient();
        using var response = await client.GetAsync(WorkerHealthProbe.ReadyPath, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var report = await response.Content.ReadFromJsonAsync<JsonElement>(TestContextToken.Value);
        report.GetProperty("status").GetString().ShouldBe("degraded");

        var checks = report.GetProperty("checks").EnumerateArray().ToList();
        checks.ShouldContain(check => check.GetProperty("name").GetString() == "database"
            && check.GetProperty("status").GetString() == "healthy");
        checks.ShouldContain(check => check.GetProperty("name").GetString() == "hangfire"
            && check.GetProperty("status").GetString() == "degraded");
    }

    /// <summary>A storage whose every read fails, i.e. a Hangfire outage with a healthy database.</summary>
    private sealed class ThrowingJobStorage : JobStorage
    {
        public override IMonitoringApi GetMonitoringApi() => throw new InvalidOperationException(Message);

        public override IStorageConnection GetConnection() => throw new InvalidOperationException(Message);

        private const string Message = "probe storage outage (09.2.6-T1)";
    }
}
