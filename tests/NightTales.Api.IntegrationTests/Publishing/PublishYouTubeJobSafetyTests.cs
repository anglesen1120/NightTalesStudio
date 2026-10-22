using NightTales.Api.IntegrationTests.Harness;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The risk classes the upload job's hand-overs created (STATUS S66-3, S69-5, S71-9, S72-1, SEC-5): a connection
/// revoked or cancelled between the schedule and the run, a publication cancelled before the job reaches it, and the
/// lock order it shares with the disconnect cascade. None of these is visible in a single-run test: the first two need
/// a row written by another path, and the third needs two processes queueing on the same two rows.
/// </summary>
/// <remarks>
/// The harness is <see cref="PublishYouTubeJobHarness"/>; the lock test holds the connection row the way
/// <c>DisconnectChannelHandler</c> does (a raw <c>FOR UPDATE</c> in an open transaction) instead of issuing the route,
/// because the point is the ordering itself, not the route's behaviour.
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
[Trait("Category", "Integration")]
public sealed class PublishYouTubeJobSafetyTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Publish_ConnectionRevokedBetweenScheduleAndRun_FailsChannelReauthRequired_WithoutGoogleOrQuota()
    {
        // STATUS S66-3/S69-5: consent does not re-check the connection, so the job must. A revoked row is the owner's
        // decision, so the publication fails for good instead of uploading (or spending quota) on a dead credential.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, revoked: true);

        await harness.RunAsync(world.PublicationId, Ct);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("failed");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.ChannelReauthRequired);
        (await harness.NextAttemptAtAsync(world.PublicationId)).ShouldBeNull();
        harness.Google.Calls.ShouldBeEmpty("a revoked connection must not reach Google at all");
        harness.Blobs.OpenReads.ShouldBe(0);
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday()) ?? 0)
            .ShouldBe(0, "the re-check runs before the reserve");
        (await harness.OutboxCountAsync("publication_updated.v1", world.PublicationId)).ShouldBe(1);
    }

    [Fact]
    public async Task Publish_CancelledPublication_IsSkippedWithoutReservingQuota()
    {
        // STATUS S72-1: the cancel path reads `quotaReservedDay`, so a row cancelled while the job was queued must
        // not reserve a unit the cancel will never release.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, status: PublicationStatus.Cancelled);

        await harness.RunAsync(world.PublicationId, Ct);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("cancelled", "a cancelled row stays cancelled");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.QuotaReservedDayKey)).ShouldBeEmpty();
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday()) ?? 0).ShouldBe(0, "no unit for a cancelled row");
        harness.Google.Calls.ShouldBeEmpty();
        harness.Blobs.OpenReads.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_OpenIncident_HoldsWithoutAnyGoogleCall_AndAClearedIncidentResumes()
    {
        // D14/YR24: every publication of the broken client is held lazily on its own run — no Google call, no quota —
        // and a held row is scheduled at its deferred instant rather than immediately. Once the incident is cleared,
        // the same row uploads on its next run.
        await using var harness = await StartAsync();
        await harness.SeedOpenIncidentAsync(ct: Ct);
        var world = await SeedReadyAsync(harness);

        await harness.RunAsync(world.PublicationId, Ct);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("scheduled");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.PlatformIncident);
        var deferred = await harness.NextAttemptAtAsync(world.PublicationId);
        deferred.ShouldBe(harness.Clock.UtcNow + TimeSpan.FromSeconds(5 * 60));
        (await harness.OutboxCountAsync("publication_updated.v1", world.PublicationId))
            .ShouldBe(1, "the deferred instant is what makes the re-check happen without a special-case guard");
        harness.Google.Calls.ShouldBeEmpty("a held row must not burn quota or fail the user's publication");
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday()) ?? 0).ShouldBe(0);

        await harness.ClearIncidentsAsync(Ct);
        harness.Clock.Set(deferred!.Value);

        await harness.RunAsync(world.PublicationId, Ct);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBeEmpty("a resumed row drops the hold's code");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.HeldByIncidentIdKey)).ShouldBeEmpty();
        harness.Google.SessionCreateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Publish_RacingADisconnectCascade_NeitherSideDeadlocks()
    {
        // SEC-5: the disconnect takes the connection row first and the publication rows second; the upload job must
        // take them in the same order. Either order is safe only if both sides agree, so the assertion is that both
        // finish: a job that locked the publication first would deadlock against the cascade and surface a
        // PostgreSQL deadlock (40P01) instead of a result.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.PersistentServerErrors;

        var failures = new List<Exception>();
        var cascade = Task.Run(async () =>
        {
            try
            {
                await using var connection = new NpgsqlConnection(Database.ConnectionString);
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(
                    TestContext.Current.CancellationToken);
                await using (var lockConnection = new NpgsqlCommand(
                                 "SELECT id FROM channel_connections WHERE id = $1 FOR UPDATE",
                                 connection,
                                 transaction))
                {
                    lockConnection.Parameters.AddWithValue(world.Channel.Id);
                    await lockConnection.ExecuteNonQueryAsync(Ct);
                }

                await using (var lockPublications = new NpgsqlCommand(
                                 "SELECT id FROM publications WHERE channel_connection_id = $1 FOR NO KEY UPDATE",
                                 connection,
                                 transaction))
                {
                    lockPublications.Parameters.AddWithValue(world.Channel.Id);
                    await lockPublications.ExecuteNonQueryAsync(Ct);
                }

                await using (var revoke = new NpgsqlCommand(
                                 "UPDATE channel_connections SET status = 'revoked', access_token_enc = NULL, "
                                 + "refresh_token_enc = NULL, revoked_at = now() WHERE id = $1",
                                 connection,
                                 transaction))
                {
                    revoke.Parameters.AddWithValue(world.Channel.Id);
                    await revoke.ExecuteNonQueryAsync(Ct);
                }

                await using (var cancel = new NpgsqlCommand(
                                 "UPDATE publications SET status = 'cancelled', updated_at = now() "
                                 + "WHERE channel_connection_id = $1 AND status IN ('awaiting_consent', 'scheduled')",
                                 connection,
                                 transaction))
                {
                    cancel.Parameters.AddWithValue(world.Channel.Id);
                    await cancel.ExecuteNonQueryAsync(Ct);
                }

                await transaction.CommitAsync(Ct);
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add(exception);
                }
            }
        }, TestContext.Current.CancellationToken);

        var upload = Task.Run(async () =>
        {
            try
            {
                await harness.RunAttemptAsync(
                    world.PublicationId,
                    attempt: 1,
                    maxAttempts: 6,
                    TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add(exception);
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(cascade, upload).WaitAsync(TimeSpan.FromSeconds(120), Ct);

        var deadlock = failures.OfType<PostgresException>().FirstOrDefault(row => row.SqlState == "40P01");
        deadlock.ShouldBeNull($"the two lock orders must agree, so neither side is a victim: {deadlock?.MessageText}");
        failures.OfType<TimeoutException>().ShouldBeEmpty("a queued statement must wait, not time out");

        // Which side won the race is the scheduler's business; both outcomes must be a state a deployment accepts.
        var status = await harness.StatusAsync(world.PublicationId);
        status.ShouldBeOneOf("cancelled", "failed", "uploading", "processing", "scheduled");
        (await harness.ChannelStatusAsync(world.Channel.Id)).ShouldBe("revoked");
    }

    private Task<PublishYouTubeJobHarness> StartAsync() =>
        PublishYouTubeJobHarness.StartAsync(Database, Ct);

    private static async Task<World> SeedReadyAsync(PublishYouTubeJobHarness harness, bool revoked = false)
    {
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(
            userId,
            status: revoked ? ChannelConnectionStatus.Revoked : ChannelConnectionStatus.Active,
            ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, PublishYouTubeJobHarness.PayloadLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(userId, episode.EpisodeId, channel.Id, ct: Ct);

        return new World(channel, publicationId);
    }

    private static async Task<World> SeedReadyAsync(
        PublishYouTubeJobHarness harness,
        PublicationStatus status)
    {
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, PublishYouTubeJobHarness.PayloadLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(
            userId,
            episode.EpisodeId,
            channel.Id,
            status: status,
            ct: Ct);

        return new World(channel, publicationId);
    }

    /// <summary>One seeded world: the connection and the publication under test.</summary>
    /// <param name="Channel">The seeded connection.</param>
    /// <param name="PublicationId">The row the test drives.</param>
    private sealed record World(PublishYouTubeJobHarness.SeededChannel Channel, Guid PublicationId);
}
