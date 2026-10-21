using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Compliance;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>
/// Review fixes of 13.3.1-T1 (group Misc): SEC-1 (<c>CreateAsync</c> refuses another user's id for a user actor) and
/// QA-5 (two handlers for one scope fail the Worker start).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeletionMiscReviewTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAsync_UserActorForAnotherUser_ReturnsNotFound_NoRowWritten()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var victim = Guid.CreateVersion7();
        var intruder = Guid.CreateVersion7();

        var denied = await CreateAsActorAsync(
            harness, victim, new TestCurrentUser { Id = intruder, Actor = ActorType.User }, DeletionScope.Account, DeletionReason.AccountDelete);

        denied.IsError.ShouldBeTrue();
        denied.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests", Ct)).ShouldBe(["0"]);
    }

    [Fact]
    public async Task CreateAsync_UserActorForItself_CreatesTheRow()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var user = Guid.CreateVersion7();

        var created = await CreateAsActorAsync(
            harness, user, new TestCurrentUser { Id = user, Actor = ActorType.User }, DeletionScope.Account, DeletionReason.AccountDelete);

        created.IsError.ShouldBeFalse();
        created.Value.UserId.ShouldBe(user);
    }

    [Theory]
    [InlineData(ActorType.Admin)]
    [InlineData(ActorType.System)]
    public async Task CreateAsync_StaffOrSystemActorForAnotherUser_StillCreatesTheRow(ActorType actor)
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var subject = Guid.CreateVersion7();

        var created = await CreateAsActorAsync(
            harness, subject, new TestCurrentUser { Id = Guid.CreateVersion7(), Actor = actor }, DeletionScope.Account, DeletionReason.AccountDelete);

        created.IsError.ShouldBeFalse();
        created.Value.UserId.ShouldBe(subject);
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM data_deletion_requests WHERE user_id = '{subject}'", Ct)).ShouldBe(["1"]);
    }

    [Fact]
    public async Task Worker_FailsToStart_WhenTwoHandlersShareAScope_NamingTheScope()
    {
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting("DataDeletion:DeferredScopes:0", "account")
            .ConfigureServices(services =>
            {
                for (var i = 0; i < 2; i++)
                {
                    services.AddScoped<IDeletionScopeHandler>(provider => new FakeDeletionScopeHandler(
                        DeletionScope.Channel,
                        new DeletionHandlerProbe(),
                        provider.GetRequiredService<IAppDbContext>(),
                        provider.GetRequiredService<ICurrentUser>()));
                }
            });

        var failure = await Should.ThrowAsync<Exception>(() => host.StartAsync(Ct));

        failure.ToString().ShouldContain("More than one IDeletionScopeHandler");
        failure.ToString().ShouldContain("channel");
    }

    /// <summary>
    /// Runs the real <see cref="DeletionRequests"/> with <paramref name="actor"/> as the ambient user, over a scope whose
    /// tenant filter is pinned to <paramref name="userId"/> so the post-insert re-read can see the row.
    /// </summary>
    private static async Task<ErrorOr.ErrorOr<DeletionRequest>> CreateAsActorAsync(
        DeletionHarness harness,
        Guid userId,
        TestCurrentUser actor,
        DeletionScope scope,
        DeletionReason reason)
    {
        using var serviceScope = harness.Host.CreateScope();
        using var pinned = serviceScope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
        var provider = serviceScope.ServiceProvider;
        var unit = provider.GetRequiredService<IUnitOfWork>();

        var requests = new DeletionRequests(
            provider.GetRequiredService<IAppDbContext>(),
            provider.GetRequiredService<IDeletionRequestWriter>(),
            provider.GetServices<IDeletionTargetOwnership>(),
            actor,
            provider.GetRequiredService<IClock>());

        await unit.BeginAsync(Ct);
        var result = await requests.CreateAsync(userId, scope, null, reason, Ct);

        if (result.IsError)
        {
            await unit.RollbackAsync(Ct);

            return result;
        }

        (await unit.CommitAsync(Ct)).IsError.ShouldBeFalse();

        return result;
    }
}
