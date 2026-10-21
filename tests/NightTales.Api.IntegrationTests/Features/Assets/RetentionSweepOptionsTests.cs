using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Features.Assets.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Assets;

/// <summary>
/// The Worker's boot-time bounds on the <c>RetentionSweep</c> section (09.7.3-T1 SEC-3): a batch size or batch
/// count outside its range fails the start with a named error instead of letting one run read or delete an
/// unbounded amount.
/// </summary>
public sealed class RetentionSweepOptionsTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Theory]
    [InlineData(nameof(RetentionSweepOptions.BatchSize), "0")]
    [InlineData(nameof(RetentionSweepOptions.BatchSize), "-1")]
    [InlineData(nameof(RetentionSweepOptions.BatchSize), "5001")]
    [InlineData(nameof(RetentionSweepOptions.MaxBatches), "0")]
    [InlineData(nameof(RetentionSweepOptions.MaxBatches), "-5")]
    [InlineData(nameof(RetentionSweepOptions.MaxBatches), "1001")]
    public async Task WorkerHost_RetentionSweepOutOfBounds_FailsTheBoot(string key, string value)
    {
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting($"{RetentionSweepOptions.SectionName}:{key}", value);

        var exception = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(Ct));

        exception.Message.ShouldContain($"{RetentionSweepOptions.SectionName}:{key}");
        exception.Message.ShouldContain("must be between 1 and");
    }

    [Theory]
    [InlineData(nameof(RetentionSweepOptions.BatchSize), "1")]
    [InlineData(nameof(RetentionSweepOptions.BatchSize), "5000")]
    [InlineData(nameof(RetentionSweepOptions.MaxBatches), "1")]
    [InlineData(nameof(RetentionSweepOptions.MaxBatches), "1000")]
    public async Task WorkerHost_RetentionSweepAtTheBounds_Boots(string key, string value)
    {
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting($"{RetentionSweepOptions.SectionName}:{key}", value);

        await host.StartAsync(Ct);
    }
}
