using ErrorOr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>QA probes for 02.4.2-T1: which rows may gate <c>Retry-After</c>, the boundaries and the options wiring.</summary>
[Trait("Category", "Integration")]
public sealed class GenerationLimitGuardQaTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RetryAfter_UncountedOlderRuns_DoNotGateTheWindow()
    {
        var scope = await NewEpisodeAsync();

        // Older than every counted run, but not counted: a platform failure and an autopilot run.
        await AddRunAsync(
            scope,
            Now - TimeSpan.FromHours(23),
            run => run.Fail("story_generation_failed", "provider_5xx", countsTowardLimit: false, Now));
        await AddRunAsync(scope, Now - TimeSpan.FromHours(22), trigger: GenerationTrigger.Autopilot);
        for (var index = 0; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(10 - index));
        }

        var result = await CheckAsync(scope);

        result.IsError.ShouldBeTrue();
        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(14 * 3600, "the oldest COUNTED run (10 h ago) gates the window");
    }

    [Fact]
    public async Task LimitGuard_RunQueuedNow_IsCounted_AndExactlyFiveRejectSixth()
    {
        var scope = await NewEpisodeAsync();
        for (var index = 0; index < 4; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index + 1));
        }

        (await CheckAsync(scope)).IsError.ShouldBeFalse("four counted: the fifth is accepted");

        await AddRunAsync(scope, Now);
        var result = await CheckAsync(scope);

        result.IsError.ShouldBeTrue("a run queued at `now` is inside the window; five counted reject the sixth");
        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(20 * 3600);
    }

    [Fact]
    public async Task LimitGuard_MoreCountedThanLimit_StillRejects_RetryAfterFromOldest()
    {
        var scope = await NewEpisodeAsync();
        for (var index = 0; index < 7; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index + 1));
        }

        var result = await CheckAsync(scope);

        result.IsError.ShouldBeTrue();
        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(17 * 3600);
    }

    [Fact]
    public async Task LimitGuard_AmbientUserDiffersFromCaller_FailsClosed()
    {
        var owner = await NewEpisodeAsync();
        var stranger = await NewEpisodeAsync();
        for (var index = 0; index < 5; index++)
        {
            await AddRunAsync(owner, Now - TimeSpan.FromHours(index + 1));
        }

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = stranger.UserId });
        var guard = new GenerationLimitGuard(db, Options.Create(new GenerationLimitOptions()), new FakeClock(Now));

        var result = await guard.CheckAsync(owner.EpisodeId, owner.UserId, Ct);

        result.IsError.ShouldBeTrue("a hidden count (tenant filter) must never read as 'under the limit' (SEC-1)");
        result.FirstError.Code.ShouldBe(ErrorCodes.InternalError);
    }

    [Fact]
    public async Task LimitGuard_NullAmbientUser_FailsClosed()
    {
        var owner = await NewEpisodeAsync();
        for (var index = 0; index < 5; index++)
        {
            await AddRunAsync(owner, Now - TimeSpan.FromHours(index + 1));
        }

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = null });
        var guard = new GenerationLimitGuard(db, Options.Create(new GenerationLimitOptions()), new FakeClock(Now));

        var result = await guard.CheckAsync(owner.EpisodeId, owner.UserId, Ct);

        result.IsError.ShouldBeTrue("an anonymous scope sees no rows; the guard must not succeed on it (SEC-1)");
        result.FirstError.Code.ShouldBe(ErrorCodes.InternalError);
    }

    [Fact]
    public void AddGenerationLimit_NonPositiveConfig_FailsOptionsValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Story:DailyGenerationLimit"] = "0" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddGenerationLimit();

        using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GenerationLimitOptions>>().Value);
    }

    [Fact]
    public void AddGenerationLimit_DefaultsWithoutConfig_Are5And24()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddGenerationLimit();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GenerationLimitOptions>>().Value;

        options.DailyGenerationLimit.ShouldBe(5);
        options.LimitWindowHours.ShouldBe(24);
    }

    private async Task<ErrorOr<Success>> CheckAsync(Scope scope)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        var guard = new GenerationLimitGuard(db, Options.Create(new GenerationLimitOptions()), new FakeClock(Now));

        return await guard.CheckAsync(scope.EpisodeId, scope.UserId, Ct);
    }

    private async Task<Scope> NewEpisodeAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new Scope(user.Id, series.Id, episode.Id);
    }

    private async Task AddRunAsync(
        Scope scope,
        DateTimeOffset queuedAt,
        Action<GenerationRun>? finish = null,
        GenerationTrigger trigger = GenerationTrigger.User)
    {
        var run = GenerationRun.Queue(
                scope.UserId,
                scope.SeriesId,
                scope.EpisodeId,
                GenerationKind.Story,
                trigger,
                null,
                EpisodeStatus.Draft,
                null,
                null,
                0m,
                queuedAt)
            .Value;

        if (finish is null)
        {
            run.Cancel(queuedAt);
        }
        else
        {
            finish(run);
        }

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(Ct);
    }

    private sealed record Scope(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
