using Xunit;
using Microsoft.Extensions.Configuration;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Series.Bible;
using NightTales.Infrastructure.Persistence;
using Shouldly;

namespace NightTales.Infrastructure.UnitTests.Persistence;

public sealed class PersistenceServiceCollectionExtensionsTests
{
    private const string ConnectionString = "Host=127.0.0.1;Port=5432;Database=nts_never_connected;Username=nts;Password=nts";

    [Fact]
    public void AddPersistence_MissingConnectionString_FailsOnStart()
    {
        var services = new ServiceCollection();

        var exception = Should.Throw<OptionsValidationException>(() => services.AddPersistence(BuildConfiguration()));

        exception.Failures.ShouldContain(failure => failure.Contains("ConnectionStrings:Postgres", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddPersistence_BlankConnectionString_FailsOnStart(string value)
    {
        var services = new ServiceCollection();

        var exception = Should.Throw<OptionsValidationException>(
            () => services.AddPersistence(BuildConfiguration(("ConnectionStrings:Postgres", value))));

        exception.Failures.ShouldContain(failure => failure.Contains("ConnectionStrings:Postgres", StringComparison.Ordinal));
    }

    [Fact]
    public void AddPersistence_DoesNotConfigureARetryingExecutionStrategy()
    {
        using var provider = BuildProvider(("ConnectionStrings:Postgres", ConnectionString));
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        // 09 README D5: a retrying strategy would silently change the UnitOfWork transaction semantics.
        context.Database.CreateExecutionStrategy().ShouldNotBeOfType<NpgsqlRetryingExecutionStrategy>();
    }

    [Fact]
    public void AddPersistence_RegistersBothContextsAsScopedServices()
    {
        using var provider = BuildProvider(("ConnectionStrings:Postgres", ConnectionString));
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<AppDbContext>().ShouldNotBeSameAs(second.ServiceProvider.GetRequiredService<AppDbContext>());
        first.ServiceProvider.GetRequiredService<IAppDbContext>().ShouldBeSameAs(first.ServiceProvider.GetRequiredService<AppDbContext>());
        first.ServiceProvider.GetRequiredService<IReadDb>().ShouldBeSameAs(first.ServiceProvider.GetRequiredService<ReadDbContext>());
    }

    [Fact]
    public void AddPersistence_RegistersTheBibleLimitsTheConfigurationBinds()
    {
        using var provider = BuildProvider(
            ("ConnectionStrings:Postgres", ConnectionString),
            ("Bible:Limits:MaxRules", "7"),
            ("Bible:Limits:MaxContentBytes", "4096"));

        // 02.6.3-T1: the Application takes the plain BibleLimits singleton, because it may not reference
        // Infrastructure and must not see IOptions<BibleOptions>.
        var limits = provider.GetRequiredService<BibleLimits>();

        limits.MaxRules.ShouldBe(7);
        limits.MaxContentBytes.ShouldBe(4096);
        limits.MaxCharacters.ShouldBe(new BibleLimits().MaxCharacters, "a key the host does not set keeps the plan's value");
    }

    [Fact]
    public void AddPersistence_ZeroBibleLimit_FailsWhenTheBibleIsResolved()
    {
        using var provider = BuildProvider(
            ("ConnectionStrings:Postgres", ConnectionString),
            ("Bible:Limits:MaxCharacters", "0"));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<BibleLimits>());

        exception.Failures.ShouldContain(failure => failure.Contains("Bible:Limits:MaxCharacters", StringComparison.Ordinal));
    }

    private static ServiceProvider BuildProvider(params (string Key, string Value)[] values)
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(values);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPersistence(configuration);

        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(entry => entry.Key, entry => (string?)entry.Value))
            .Build();
}
