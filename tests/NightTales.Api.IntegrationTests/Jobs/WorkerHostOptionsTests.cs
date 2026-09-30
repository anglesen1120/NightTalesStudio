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
/// The Worker's start-time validation (09.2.6-T1): a wrong <c>Jobs</c> value, or a connection string the
/// harness refuses, must fail the boot with a named error instead of silently starting a server that
/// polls the wrong schema or writes to a database this machine does not own.
/// </summary>
/// <remarks>
/// Every host below fails inside <c>StartAsync</c> and is still disposed by <c>await using</c>: a failed
/// start is stopped best effort by the harness, and disposing it must not mask the boot error the test
/// asserts. That contract is pinned by
/// <see cref="WorkerHostTests.WorkerHost_FailedStart_StopsTheStartedServices_AndKeepsTheBootError"/>.
/// </remarks>
public sealed class WorkerHostOptionsTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task WorkerHost_ZeroWorkerCount_FailsTheBoot()
    {
        await using var host = new WorkerHost(Database, new FakeClock())
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
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting($"{JobsOptions.SectionName}:{nameof(JobsOptions.Workers)}:fetch", "1");

        var exception = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain("may only name queues");
    }

    [Fact]
    public async Task WorkerHost_BlankSchemaName_FailsTheBoot()
    {
        await using var host = new WorkerHost(Database, new FakeClock())
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

    [Theory]
    [InlineData("hangfire; drop schema public")]
    [InlineData("\"Hangfire\"")]
    [InlineData("public.hangfire")]
    public async Task WorkerHost_SchemaNameOutsideTheIdentifierRule_FailsTheBoot(string schemaName)
    {
        // Hangfire's own schema SQL interpolates the name (SEC 09.2.6-T1/SEC-02), so only a lower-case,
        // unquoted identifier may reach it.
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting($"{JobsOptions.SectionName}:{nameof(JobsOptions.SchemaName)}", schemaName);

        var exception = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain("lower-case, unquoted PostgreSQL identifier");
    }

    [Fact]
    public async Task WorkerHost_NonLoopbackServer_FailsTheBoot()
    {
        // The Worker graph installs Hangfire's schema (PrepareSchemaIfNecessary), so the harness hands
        // it the same loopback boundary as the fixture's own DDL (SEC 09.2.6-T1/SEC-01).
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting("ConnectionStrings:Postgres", RemoteHostConnectionString);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain("not a loopback host");
    }

    [Fact]
    public async Task WorkerHost_UnmanagedDatabaseName_FailsTheBoot()
    {
        // A loopback server is not enough: the database must carry the harness's reserved prefix, or a
        // typo could let the Worker install its schema in a database the harness does not own.
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting("ConnectionStrings:Postgres", UnmanagedDatabaseConnectionString);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain(PostgresFixture.ManagedDatabasePrefix);
    }

    private const string RemoteHostConnectionString =
        "Host=nts-remote.invalid;Port=5432;Database=nts_remote;Username=nts;Password=nts-not-a-real-password";

    private const string UnmanagedDatabaseConnectionString =
        "Host=127.0.0.1;Port=5432;Database=nighttales;Username=nts;Password=nts-not-a-real-password";
}
