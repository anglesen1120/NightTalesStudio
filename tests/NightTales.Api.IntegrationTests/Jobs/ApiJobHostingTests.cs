using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The real Api host never runs a Hangfire server (09 README D12/H4): the only job port the Api
/// composition root registers is the throwing scheduler, because jobs belong to the Worker.
/// </summary>
public sealed class ApiJobHostingTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public void Api_NeverRegistersAHangfireServer()
    {
        using var factory = new ApiFactory(Database, new FakeClock());

        var hostedServices = factory.Services.GetServices<IHostedService>().ToList();

        hostedServices.ShouldNotContain(
            service => service.GetType().FullName == "Hangfire.BackgroundJobServerHostedService");
        factory.Services.GetRequiredService<IJobScheduler>().ShouldBeOfType<ThrowingJobScheduler>();
    }
}
