using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Infrastructure.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The Worker's start-time configuration validation (09.2.6-T1): a wrong <c>Jobs</c> value must
/// fail the boot with a named options error instead of silently starting a server that polls the
/// wrong schema or the wrong queues.
/// </summary>
/// <remarks>
/// These hosts are not disposed: <c>WorkerHost.DisposeAsync</c> today crashes in
/// <c>Host.StopAsync</c> when <c>StartAsync</c> threw while resolving the hosted services
/// (QA-926T1-D1) — disposing would mask the options error the test asserts. A host that never
/// started holds no database connection, so nothing real leaks.
/// </remarks>
public sealed class WorkerHostOptionsTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task WorkerHost_ZeroWorkerCount_FailsTheBoot()
    {
        var host = new WorkerHost(Database, new FakeClock())
            .WithSetting($"{JobsOptions.SectionName}:{nameof(JobsOptions.Workers)}:default", "0");

        var exception = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain("Jobs");
        exception.Message.ShouldContain("greater than zero");
    }

    [Fact]
    public async Task WorkerHost_UnknownQueueName_FailsTheBoot()
    {
        // `Jobs:Workers:fetch` names no JobQueue member: the server must refuse to start rather than
        // count workers for a queue it never listens on.
        var host = new WorkerHost(Database, new FakeClock())
            .WithSetting($"{JobsOptions.SectionName}:{nameof(JobsOptions.Workers)}:fetch", "1");

        var exception = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain("may only name queues");
    }

    [Fact]
    public async Task WorkerHost_BlankSchemaName_FailsTheBoot()
    {
        var host = new WorkerHost(Database, new FakeClock())
            .WithSetting($"{JobsOptions.SectionName}:{nameof(JobsOptions.SchemaName)}", " ");

        var exception = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain("must not be blank");
    }

    [Fact]
    public async Task WorkerHost_MissingConnectionString_FailsTheBoot()
    {
        // The composition root refuses a blank platform connection string before it reaches
        // Hangfire — the same fail-fast contract AddPersistence carries (09 README D8). This host
        // is disposable: the exception is thrown while the service collection is built, so the
        // host object is never assigned.
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting("ConnectionStrings:Postgres", " ");

        var exception = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain("ConnectionStrings:Postgres");
    }
}
