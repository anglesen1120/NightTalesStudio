using System.Net;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// The error the FR-02-7 guard returns (02.4.2-T1) reaches the client as 429 <c>story_generation_limit</c> with a
/// <c>Retry-After</c> header equal to the body's <c>retryAfterSeconds</c> (D8), through the real
/// <see cref="ProblemDetailsWriter"/>. The endpoint that calls the guard is 02.2.2-T2's, which owns the
/// end-to-end <c>Generate_SixthIn24h_Returns429StoryGenerationLimitWithRetryAfter</c> over HTTP.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenerationLimitProblemTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GuardError_AtLimit_Maps429WithMatchingHeaderAndBody()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        for (var index = 0; index < 5; index++)
        {
            // Hours 5..1 ago: the oldest (5 h) leaves the 24 h window in 19 h = 68 400 s.
            var run = GenerationRun.Queue(
                    user.Id,
                    series.Id,
                    episode.Id,
                    GenerationKind.Story,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    null,
                    null,
                    0m,
                    Now - TimeSpan.FromHours(5 - index))
                .Value;
            run.Cancel(Now);

            await using var insert = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id });
            insert.GenerationRuns.Add(run);
            await insert.SaveChangesAsync(Ct);
        }

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id });
        var guard = new GenerationLimitGuard(db, Options.Create(new GenerationLimitOptions()), new FakeClock(Now));
        var verdict = await guard.CheckAsync(episode.Id, user.Id, Ct);
        verdict.IsError.ShouldBeTrue();

        using var exchange = new ProblemExchange();
        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(verdict.Errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exchange.Context.Response.Headers.RetryAfter.ToString().ShouldBe("68400");
        response.ShouldNotBeNull();
        response.Code.ShouldBe("story_generation_limit");
        response.Metadata.ShouldNotBeNull();
        response.Metadata["retryAfterSeconds"].GetInt32().ShouldBe(68_400);
        response.Metadata["limit"].GetInt32().ShouldBe(5);
    }
}
