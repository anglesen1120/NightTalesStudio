using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence.Seeding;

/// <summary>
/// The 09.2.5-T1 failure path (review findings). A seeder that throws under
/// <see cref="SeedOptions.ContinueOnError"/> must leave a failed <see cref="SeedReport"/>, must not
/// leave its pending entities on the shared tracker, and must still make <c>nts-seed</c> exit non-zero.
/// </summary>
public sealed class SeedFailureHandlingTests
{
    private const string NeverConnected = "Host=127.0.0.1;Port=5432;Database=nts_seed_never_connected;Username=nts;Password=nts";

    [Fact]
    public void SeedCommand_SeederFailsUnderContinueOnError_ExitsOne()
    {
        using var provider = BuildProvider(new ThrowingSeeder("voices", order: 10));

        SeedCommand.Run(provider).ShouldBe(1);
    }

    [Fact]
    public void SeedCommand_EverySeederSucceeds_ExitsZero()
    {
        using var provider = BuildProvider(new SucceedingSeeder("voices", order: 10));

        SeedCommand.Run(provider).ShouldBe(0);
    }

    [Fact]
    public async Task SeedRunner_FailedSeeder_ReportsFailureAndClearsPendingChanges()
    {
        await using var db = TestDbContextFactory.CreateAppContext(NeverConnected);
        var runner = new SeedRunner(
            [new ThrowingSeeder("voices", order: 10, db), new SucceedingSeeder("feature_flags", order: 20)],
            db,
            Options.Create(new SeedOptions { ContinueOnError = true }),
            NullLogger<SeedRunner>.Instance);

        var reports = await runner.RunAsync(TestContext.Current.CancellationToken);

        // The run carries past the failure, but the failed seeder is still visible in the reports.
        reports.Length.ShouldBe(2);
        reports.Single(report => report.Seeder == "voices").Failed.ShouldBeTrue();
        reports.Single(report => report.Seeder == "feature_flags").ShouldBe(new SeedReport("feature_flags", 1, 0, 0));

        // The failed seeder's Added row must not survive into the next seeder's SaveChanges.
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    private static ServiceProvider BuildProvider(params ISeeder[] seeders)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => TestDbContextFactory.CreateAppContext(NeverConnected));
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());
        services.AddSingleton(Options.Create(new SeedOptions { ContinueOnError = true }));
        services.AddSingleton<ILogger<SeedRunner>>(NullLogger<SeedRunner>.Instance);

        foreach (var seeder in seeders)
        {
            services.AddScoped(_ => seeder);
        }

        services.AddScoped<SeedRunner>();

        return services.BuildServiceProvider();
    }

    /// <summary>A seeder that fails after leaving a pending Added row, the leak the review found.</summary>
    private sealed class ThrowingSeeder(string name, int order, AppDbContext? db = null) : ISeeder
    {
        public string Name => name;

        public int Order => order;

        public ValueTask<SeedReport> SeedAsync(CancellationToken ct)
        {
            db?.Voices.Add(Voice.Create("pending-voice", "Pending", Language.Vi, VoiceGender.Female));

            throw new SeedValidationException($"seed_test:{name}:boom");
        }
    }

    private sealed class SucceedingSeeder(string name, int order) : ISeeder
    {
        public string Name => name;

        public int Order => order;

        public ValueTask<SeedReport> SeedAsync(CancellationToken ct) => ValueTask.FromResult(new SeedReport(name, 1, 0, 0));
    }
}
