using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Security;
using NightTales.Worker;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Composition;

/// <summary>
/// 11.1.1-T1 regression: the real Worker host must build in Development, where the host validates the
/// dependency-injection graph. Mediator's source generator registers every Application handler in the
/// Worker, including the Api-only auth handlers whose <c>IAuthSessionService</c>/<c>IRequestInfoAccessor</c>
/// the Worker never composes, so build-time graph validation would otherwise fail every Development boot.
/// The fix keeps scope validation and leaves build validation off; the Worker's own job graph is
/// validated by <c>BackgroundJobActivationCheck</c> at start.
/// </summary>
public sealed class WorkerCompositionTests
{
    /// <summary>
    /// A parseable connection string <c>AddJobs</c> requires while it registers; <c>Build</c> never
    /// connects, so the value is never used.
    /// </summary>
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=5432;Database=nighttales_worker_composition;Username=nts;Password=nts";

    [Fact]
    public void WorkerHost_BuildsInDevelopment_AndKeepsScopeValidation()
    {
        var args = BuildArgs();

        // Before 11.1.1-T1 this threw: the host's Development validate-on-build walked the Mediator
        // handlers and could not resolve IAuthSessionService or IRequestInfoAccessor.
        using var app = WorkerApplication.Build(args);

        // The fix turns off build validation only: resolving a scoped service from the root provider
        // must still fail, which is what proves scope validation stayed on.
        Should.Throw<InvalidOperationException>(() => app.Services.GetRequiredService<ICurrentUser>());
    }

    /// <summary>
    /// 11.1.1-T1 review fix: the health listener's bind address is configurable. The native stack keeps
    /// the loopback default; the optional Compose service sets <c>0.0.0.0</c> because a published host
    /// port reaches the container's network interface, not its loopback (the published host side stays
    /// <c>127.0.0.1</c>).
    /// </summary>
    [Fact]
    public void WorkerHealthAddress_DefaultsToLoopback()
    {
        WorkerOptions.DefaultHealthAddress.ShouldBe("127.0.0.1");
        WorkerOptions.ResolveHealthAddress(null).ShouldBe(IPAddress.Loopback);
        WorkerOptions.ResolveHealthAddress("   ").ShouldBe(IPAddress.Loopback);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    public void WorkerHealthAddress_AcceptsIpLiterals(string configured)
    {
        WorkerOptions.ResolveHealthAddress(configured).ShouldBe(IPAddress.Parse(configured));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("0.0.0.0:8082")]
    [InlineData("http://0.0.0.0")]
    public void WorkerHealthAddress_RejectsNonIpLiterals(string configured)
    {
        var error = Should.Throw<InvalidOperationException>(() => WorkerOptions.ResolveHealthAddress(configured));
        error.Message.ShouldContain("Worker:HealthAddress");
    }

    [Fact]
    public void WorkerHost_BuildsWithTheComposeHealthAddress()
    {
        // The Compose service's value must be accepted by the real host composition.
        using var app = WorkerApplication.Build(BuildArgs("--Worker:HealthAddress", "0.0.0.0"));

        app.ShouldNotBeNull();
    }

    private static string[] BuildArgs(params string[] extra)
    {
        var args = new List<string>
        {
            "--environment", Environments.Development,
            $"--ConnectionStrings:{PersistenceOptions.ConnectionStringName}={UnusedConnectionString}",
            // AddWorkerServices loads the DP_KEK PFX while it registers (09.9.4-T1), so Build needs it.
            $"--{TestKek.PathConfigurationKey}={TestKek.SharedPath}",
            $"--{TestKek.PasswordConfigurationKey}={TestKek.SharedPassword}",
        };
        args.AddRange(extra);
        return [.. args];
    }
}
