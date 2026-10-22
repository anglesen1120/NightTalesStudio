using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// A refused commit is never a verdict (06.2.3-T2 §Failure handling, QA D1): every run of
/// <c>PublishYouTubeJob</c> that loses its transaction asks Hangfire for another attempt instead of reporting a
/// decided <c>ErrorOr</c> the job swallows, and the quota ledger keeps exactly the units that were really spent.
/// </summary>
/// <remarks>
/// <para>
/// The refusal is staged where a transient failure happens in a deployment: a scoped <see cref="IUnitOfWork"/>
/// decorator that rolls the real unit of work back and answers the error <c>EfUnitOfWork</c> answers for one
/// (<c>service_unavailable</c>-shaped), on the nth commit of the scope. The harness's <c>services</c> hook replaces the
/// registration after the production composition, so nothing else about the run changes.
/// </para>
/// <para>
/// The two cases split on where the unit was spent, which is the whole release rule: the session-URI commit fails after
/// <c>PrepareAsync</c> committed this run's reservation (so it goes back), while a refused <c>PrepareAsync</c> commit
/// rolls the reservation back with the transaction (so a release there would take another row's unit).
/// </para>
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class PublishYouTubeJobCommitFailureTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task RefusedCommitAtTheSessionUriStep_AsksForARetryAndReleasesTheUnit()
    {
        // §Failure handling: "SaveChanges fails at the session-URI commit: the handler aborts before the first byte,
        // releases the unit and lets Hangfire retry — the safe direction." The create already reached Google, but its
        // session is unrecoverable (the cipher rolled back with the transaction), so the row must be retried and the
        // unit this run reserved must not be stranded on an upload nobody can resume.
        // Commit 1 is the prepare, 2 the session-create claim (SEC-8) and 3 the session-URI commit this test refuses.
        await using var harness = await StartAsync(refuseCommitNumber: 3);
        var world = await SeedReadyAsync(harness);

        await ShouldAskForARetryAsync(harness, world.PublicationId);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe(
            "uploading",
            "the state move of the first transaction committed, so the row keeps its `uploading` claim");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.UploadSessionEncKey)).ShouldBe(
            string.Empty,
            "the cipher was written in the refused transaction, so nothing can address the session it named");
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday())).ShouldBe(
            0,
            "the unit this run reserved was committed before the create, so the attempt gives it back");
        (await harness.PayloadKeyKindAsync(world.PublicationId, PublicationPayload.QuotaReservedDayKey)).ShouldBe(
            string.Empty,
            "the day key names the unit, so it leaves with the release: a stale key would let the retry skip its own "
            + "reservation and a later cancel give the same unit back twice");
        (await harness.NextAttemptAtAsync(world.PublicationId)).ShouldBeNull(
            "a retry is Hangfire's, not a reschedule");
        harness.Google.SessionCreateCalls.ShouldBe(
            1,
            "the refused commit aborts the run; no second create happens on this attempt");
    }

    [Fact]
    public async Task RefusedCommitAtThePrepareStep_AsksForARetryAndLeavesAnotherRowsUnitAlone()
    {
        // The mirror: the reservation of this run was made inside the very transaction that was refused, so it rolled
        // back with it. `IQuotaLedger.ReleaseAsync` decrements unconditionally, so releasing here would take a unit
        // another row is holding — the rule the session-URI case must not be copied to.
        await using var harness = await StartAsync(refuseCommitNumber: 1);
        var world = await SeedReadyAsync(harness);
        var day = harness.QuotaDayToday();
        await harness.SeedQuotaAsync(day, unitsUsed: 1, unitsLimit: 100, ct: Ct);

        await ShouldAskForARetryAsync(harness, world.PublicationId);

        (await harness.QuotaUnitsAsync(day)).ShouldBe(
            1,
            "the other row's unit survives: this run's reserve rolled back with the refused commit");
        (await harness.StatusAsync(world.PublicationId)).ShouldBe(
            "scheduled",
            "the `scheduled → uploading` move belonged to the refused transaction");
        (await harness.PayloadKeyKindAsync(world.PublicationId, PublicationPayload.QuotaReservedDayKey)).ShouldBe(
            string.Empty,
            "the reserve's `quotaReservedDay` never landed, so nothing is released or skipped");
        (await harness.NextAttemptAtAsync(world.PublicationId)).ShouldBeNull(
            "a retry is Hangfire's, not a reschedule");
        harness.Google.SessionCreateCalls.ShouldBe(0, "the run never reached the insert");
        harness.Google.ProbeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RefusedCommitAtTheSessionUriStep_OnTheLastAttempt_EndsTheRowAndReleasesTheUnit()
    {
        // F2/SEC-2: nothing follows the sixth attempt, so the refusal must not leave the row `uploading` with nothing
        // left to run it. The row gets the verdict plan §7 assigns to spent attempts, and the unit that bought a session
        // nobody can address goes back (YR19's exception for a create that reached Google — QA D1).
        await using var harness = await StartAsync(refuseCommitNumber: 3);
        var world = await SeedReadyAsync(harness);

        var thrown = await Should.ThrowAsync<PublishAttemptException>(
            () => harness.RunAttemptAsync(world.PublicationId, attempt: 6, maxAttempts: 6, Ct));

        thrown.Code.ShouldBe(
            PublishAttemptException.InfrastructureCode,
            "the refusal is still reported as an infrastructure failure of the attempt");
        (await harness.StatusAsync(world.PublicationId)).ShouldBe(
            "failed",
            "the attempts are spent, so the row must not be left with a state nothing will ever pick up");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.UploadFailed);
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday())).ShouldBe(
            0,
            "the create bought a session the row cannot address, so the unit goes back");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.QuotaReservedDayKey)).ShouldBeEmpty();
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.UploadCreateIssuedAtKey)).ShouldBeEmpty();
        harness.Google.SessionCreateCalls.ShouldBe(1);
    }

    /// <summary>Runs the attempt and asserts the one outcome a refused commit may produce: another attempt.</summary>
    /// <param name="harness">The world.</param>
    /// <param name="publicationId">The row.</param>
    private static async Task ShouldAskForARetryAsync(PublishYouTubeJobHarness harness, Guid publicationId)
    {
        var thrown = await Should.ThrowAsync<PublishAttemptException>(
            () => harness.RunAttemptAsync(publicationId, attempt: 1, maxAttempts: 6, Ct));

        thrown.Code.ShouldBe(
            PublishAttemptException.InfrastructureCode,
            "a refused commit is an infrastructure failure of the attempt, not a retryable Google answer");
    }

    private Task<PublishYouTubeJobHarness> StartAsync(int refuseCommitNumber) =>
        PublishYouTubeJobHarness.StartAsync(
            Database,
            Ct,
            services: collection =>
            {
                collection.RemoveAll<IUnitOfWork>();
                collection.AddScoped<IUnitOfWork>(provider => new RefusingCommitUnitOfWork(
                    ActivatorUtilities.CreateInstance<EfUnitOfWork>(provider),
                    refuseCommitNumber));
            });

    private static async Task<World> SeedReadyAsync(PublishYouTubeJobHarness harness)
    {
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(userId, episode.EpisodeId, channel.Id, ct: Ct);

        return new World(userId, channel, episode, publicationId);
    }

    /// <summary>
    /// A scoped <see cref="IUnitOfWork"/> whose <paramref name="refusedCommit"/>'th commit ends the transaction the way
    /// a transient provider failure does: the real unit of work rolls back and the caller reads an error, so nothing
    /// the run wrote is persisted.
    /// </summary>
    /// <param name="inner">The real unit of work of this scope.</param>
    /// <param name="refusedCommit">The 1-based commit number to refuse.</param>
    private sealed class RefusingCommitUnitOfWork(EfUnitOfWork inner, int refusedCommit) : IUnitOfWork
    {
        private int _commits;

        public Task BeginAsync(CancellationToken cancellationToken) => inner.BeginAsync(cancellationToken);

        public async Task<ErrorOr<Success>> CommitAsync(CancellationToken cancellationToken)
        {
            _commits++;

            if (_commits == refusedCommit)
            {
                await inner.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Error.Failure("service_unavailable", "Simulated transient commit failure.");
            }

            return await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task RollbackAsync(CancellationToken cancellationToken) => inner.RollbackAsync(cancellationToken);
    }

    /// <summary>One seeded world: the owner, its connection, the ready episode and the publication under test.</summary>
    /// <param name="UserId">The owner.</param>
    /// <param name="Channel">The seeded connection.</param>
    /// <param name="Episode">The seeded episode with its final video.</param>
    /// <param name="PublicationId">The row the test drives.</param>
    private sealed record World(
        Guid UserId,
        PublishYouTubeJobHarness.SeededChannel Channel,
        PublishYouTubeJobHarness.SeededEpisode Episode,
        Guid PublicationId);
}
