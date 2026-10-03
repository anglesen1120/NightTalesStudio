using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Features.Stories.Generation;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>QA probe for 02.4.2-T1: the Api composition resolves the scoped guard (and its options) end to end.</summary>
[Trait("Category", "Integration")]
public sealed class GenerationLimitWiringTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public void ApiHost_ResolvesGenerationLimitGuard_AndDefaultOptions()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IGenerationLimitGuard>().ShouldNotBeNull();
        var options = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<GenerationLimitOptions>>().Value;
        options.DailyGenerationLimit.ShouldBe(5);
        options.LimitWindowHours.ShouldBe(24);
    }
}
