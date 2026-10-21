using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Telemetry;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// <c>DELETE /api/v1/channels/{id}</c> over the real Api, a real PostgreSQL 17 and the real
/// <c>GoogleYouTubeClient</c> pointed at <see cref="FakeGoogleServer"/> (06.4.1-T1, 06 README D15/D17/D18):
/// the route answers <c>204</c>, revokes the grant with one form-body <c>POST /revoke</c>, wipes both token
/// ciphertexts whatever Google answers, schedules the deletion request, raises one
/// <c>channel_connection_changed.v1</c> row and one ids-only audit row — and a repeat, a foreign id and a
/// flag-off host write nothing.
/// </summary>
/// <remarks>
/// <para>
/// The cascade's assertions (quota release, the channel-scope deletion request's columns, the redaction of
/// every written document) live in <see cref="DisconnectChannelCascadeTests"/>; both classes share
/// <see cref="DisconnectChannelHarness"/> and run in the <c>Process-global host state</c> collection, because
/// two of these tests read a process-wide <see cref="MeterListener"/> of the <c>NightTales</c> meter.
/// </para>
/// <para>
/// The retry test below is the one that spends the ladder's own wall clock (~1.25–1.5 s of backoff between
/// three attempts): the host keeps <c>TimeProvider.System</c> on purpose, so what it proves is the delay
/// policy the contract fixes, not a fake that never waits.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Collection("Process-global host state")]
[Trait("Category", "Integration")]
public sealed class DisconnectChannelTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Disconnect_RevokesAtGoogleThenWipes_TokensNullInDb()
    {
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        using var client = harness.ClientFor(userId);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        // Acceptance 1: 204, the row revoked, both ciphertexts gone and revoked_at set.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, text);
        text.ShouldBeEmpty("a 204 carries no body: a channel has no DTO and no ETag (task §The 204 choice)");

        harness.ChannelHas(channel.Id, "status = 'revoked'").ShouldBeTrue("the aggregate's Revoke ran");
        harness.ChannelHas(channel.Id, "access_token_enc IS NULL AND refresh_token_enc IS NULL")
            .ShouldBeTrue("both ciphertexts are wiped whatever Google answered (R10)");
        harness.ChannelHas(channel.Id, $"revoked_at = {DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start)}")
            .ShouldBeTrue("revoked_at is the command's single instant");
        harness.ChannelHas(channel.Id, $"token_expires_at = {DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start)}")
            .ShouldBeTrue("Revoke moves the expiry to the disconnect moment too");

        // Acceptance 2: exactly one revoke, the token in the form body, never in the URL.
        harness.Google.RevokeCount.ShouldBe(1, "one successful revoke is one attempt");
        var revoke = harness.Google.RevokeRequests.ShouldHaveSingleItem();
        revoke.Method.ShouldBe("POST");
        revoke.PathAndQuery.ShouldBe(
            FakeGoogleServer.RevokePath,
            "Google's revoke form post carries no query string at all");
        revoke.ContentType.ShouldNotBeNull().ShouldStartWith("application/x-www-form-urlencoded");
        revoke.FormValue("token").ShouldBe(
            DisconnectChannelHarness.RefreshToken,
            "the refresh token is the long-lived grant: revoking it kills the access token with it");
        revoke.PathAndQuery.ShouldNotContain(DisconnectChannelHarness.RefreshToken, Case.Sensitive);
        revoke.PathAndQuery.ShouldNotContain(DisconnectChannelHarness.AccessToken, Case.Sensitive);

        // The audit projection names the answer Google gave, with ids and counts only.
        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        using (var data = JsonDocument.Parse(audit))
        {
            var root = data.RootElement;
            root.GetProperty("googleRevoke").GetString().ShouldBe("revoked");
            root.GetProperty("platform").GetString().ShouldBe("youtube");
            root.GetProperty("externalAccountId").GetString().ShouldBe(channel.ExternalAccountId);
            root.GetProperty("oauthClientId").GetString().ShouldNotBeNullOrWhiteSpace();
            root.GetProperty("publicationsCancelled").GetInt32().ShouldBe(0);
            root.GetProperty("quotaUnitsReleased").GetInt32().ShouldBe(0);
        }

        harness.AuditHas(channel.Id, "entity_type = 'channel_connection'")
            .ShouldBeTrue("the audit row names the entity type the connect command spelled for this row");

        // D13: one channel_connection_changed.v1 with the ids and the new state.
        var payloads = harness.ConnectionPayloads(channel.Id).ShouldHaveSingleItem();
        using (var payload = JsonDocument.Parse(payloads))
        {
            var root = payload.RootElement;
            root.EnumerateObject().Select(property => property.Name).Order().ShouldBe(
                ["connectionId", "occurredAt", "status", "userId"]);
            root.GetProperty("connectionId").GetGuid().ShouldBe(channel.Id);
            root.GetProperty("userId").GetGuid().ShouldBe(userId);
            root.GetProperty("status").GetString().ShouldBe("revoked");
        }

        harness.DeletionRequestCount(userId).ShouldBe(1, "the same transaction schedules the channel-scope purge");
    }

    [Fact]
    public async Task Disconnect_GoogleSaysAlreadyRevoked_IsSuccess()
    {
        // Google answers 400 {"error":"invalid_token"}: the grant is already gone, which is the state the
        // caller wanted, so it is a success — one attempt, no retry, no failure metric.
        using var counter = new RevokeFailureCounter();
        await using var harness = await StartAsync(new FakeGoogleOptions { RevokeStatusCode = HttpStatusCode.BadRequest });
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        using var client = harness.ClientFor(userId);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        harness.Google.RevokeCount.ShouldBe(1, "an already-revoked token is a final answer, not a retryable one (Acceptance 4)");
        harness.ChannelHas(channel.Id, "status = 'revoked' AND access_token_enc IS NULL AND refresh_token_enc IS NULL")
            .ShouldBeTrue("the local end state is the same as any other successful disconnect");

        counter.Readings.ShouldBe(
            0,
            "invalid_token is not a failure: youtube_channel_revoke_failed_total must not move (Acceptance 4)");

        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        using var data = JsonDocument.Parse(audit);
        data.RootElement.GetProperty("googleRevoke").GetString().ShouldBe("alreadyRevoked");
    }

    [Fact]
    public async Task Disconnect_GoogleRevokeFailsAfterRetries_StillWipesAndAlerts()
    {
        // The whole ladder, on the real TimeProvider: three attempts, 250 ms + 1 s of backoff between them,
        // then the disconnect continues anyway (R10).
        using var counter = new RevokeFailureCounter();
        await using var harness = await StartAsync(new FakeGoogleOptions { RevokeStatusCode = HttpStatusCode.InternalServerError });
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        using var client = harness.ClientFor(userId);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "a Google failure never fails the owner's disconnect (Acceptance 3): "
            + await response.Content.ReadAsStringAsync(Ct));

        harness.Google.RevokeCount.ShouldBe(3, "three attempts in total: the first is immediate, two are retried");
        harness.ChannelHas(channel.Id, "status = 'revoked' AND access_token_enc IS NULL AND refresh_token_enc IS NULL")
            .ShouldBeTrue("the tokens are wiped even though Google never revoked them");

        counter.Total.ShouldBe(1, "youtube_channel_revoke_failed_total is incremented once per failed disconnect");
        counter.Readings.ShouldBe(1, "one measurement, not one per attempt");

        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        using (var data = JsonDocument.Parse(audit))
        {
            data.RootElement.GetProperty("googleRevoke").GetString().ShouldBe("failed");
        }

        harness.Logs.CountOf(DisconnectChannelHarness.RevokeFailedEventId)
            .ShouldBe(1, "the exhaustion is logged once, with the attempt count and no token");
        harness.DeletionRequestCount(userId).ShouldBe(1, "the purge is scheduled whatever Google answered");
    }

    [Fact]
    public async Task Disconnect_SecondCall_Returns204_NoSecondRequest()
    {
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var publication = await harness.SeedPublicationAsync(userId, channel.Id, ct: Ct);
        using var client = harness.ClientFor(userId);

        using (var first = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync(Ct));
        }

        var writtenRow = harness.ChannelRow(channel.Id);

        using (var repeat = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct))
        {
            repeat.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                "the lost-response repeat answers what the first attempt answered (D15/R3): "
                + await repeat.Content.ReadAsStringAsync(Ct));
        }

        // Acceptance 5: one row in each of the three tables the repeat could duplicate.
        harness.DeletionRequestCount(userId).ShouldBe(1);
        harness.AuditData(channel.Id).ShouldHaveSingleItem();
        harness.ConnectionPayloads(channel.Id).ShouldHaveSingleItem();

        harness.Google.RevokeCount.ShouldBe(
            1,
            "the repeat holds no ciphertext left to revoke, so it never calls Google again (Design step 2)");
        harness.ChannelRow(channel.Id).ShouldBe(writtenRow, "the repeat changes nothing on the row");
        harness.PublicationStatusOf(publication.Id).ShouldBe("cancelled");
        harness.PublicationPayloads(publication.Id).ShouldHaveSingleItem(
            "the cascade cancelled the row once, so the repeat raises no second publication event");
    }

    [Fact]
    public async Task Disconnect_OtherUsersChannel_Returns404()
    {
        await using var harness = await StartAsync();
        var owner = await harness.CreateUserAsync(Ct);
        var stranger = await harness.CreateUserAsync(Ct);
        var victim = await harness.SeedChannelAsync(owner, ct: Ct);
        var mine = await harness.SeedChannelAsync(stranger, ct: Ct);
        using var client = harness.ClientFor(stranger);

        var victimRowBefore = harness.ChannelRow(victim.Id);
        var mineRowBefore = harness.ChannelRow(mine.Id);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, victim.Id, Ct);
        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);

        problem.Code.ShouldBe("not_found", "another owner's id is 404, never 403 (09 README D10)");

        // Acceptance 8: the victim is untouched and Google is never called for someone else's channel.
        harness.ChannelRow(victim.Id).ShouldBe(victimRowBefore, "the refused disconnect writes nothing to the row");
        harness.AuditData(victim.Id).ShouldBeEmpty("the refused disconnect writes no audit row");
        harness.ConnectionPayloads(victim.Id).ShouldBeEmpty("the refused disconnect raises no event");
        harness.DeletionRequestCount(owner).ShouldBe(0, "no deletion request is scheduled for a row the caller does not own");
        harness.Google.Calls.ShouldBeEmpty("a foreign id never reaches Google");
        harness.Google.RevokeCount.ShouldBe(0);

        // The caller's own row is untouched too: the 404 came from the locked read, not from a partial write.
        harness.ChannelRow(mine.Id).ShouldBe(mineRowBefore, "the refused call never touches the caller's own row");
        harness.ChannelHas(mine.Id, "status = 'active'").ShouldBeTrue();
        harness.ChannelRowCount(victim.Id).ShouldBe(1, "a refused disconnect never deletes the row either");
    }

    [Fact]
    public async Task Disconnect_FlagOff_Returns404FeatureDisabled()
    {
        await using var harness = await StartAsync(flagOn: false);
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        using var client = harness.ClientFor(userId);

        var rowBefore = harness.ChannelRow(channel.Id);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);

        problem.Code.ShouldBe("feature_disabled", "a disabled feature answers like a route that does not exist (BD13)");

        // Acceptance 9: zero Google calls and an unchanged row.
        harness.Google.Calls.ShouldBeEmpty("the feature filter runs before the handler");
        harness.Google.RevokeCount.ShouldBe(0);
        harness.ChannelRow(channel.Id).ShouldBe(rowBefore);
        harness.DeletionRequestCount(userId).ShouldBe(0);
    }

    [Fact]
    public async Task Disconnect_UnreadableTokenCipher_SkipsGoogleAndWipes()
    {
        // The key ring rotated away: neither cipher opens, so Google cannot be called — and the disconnect
        // still wipes the row and schedules the purge (task §Failure handling, "unprotectable token").
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(
            userId,
            accessTokenCipher: DisconnectChannelHarness.UnreadableCipher,
            refreshTokenCipher: DisconnectChannelHarness.UnreadableCipher,
            ct: Ct);
        using var client = harness.ClientFor(userId);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        harness.ChannelHas(channel.Id, "status = 'revoked' AND access_token_enc IS NULL AND refresh_token_enc IS NULL")
            .ShouldBeTrue("an unreadable pair is wiped like any other");
        harness.Google.Calls.ShouldBeEmpty("no plaintext token means no revoke call at all");
        harness.Logs.CountOf(DisconnectChannelHarness.RevokeSkippedEventId)
            .ShouldBe(1, "the skip is a warning carrying the connection id, never the cipher");

        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        using var data = JsonDocument.Parse(audit);
        data.RootElement.GetProperty("googleRevoke").GetString().ShouldBe("skipped");
    }

    [Fact]
    public async Task Disconnect_NullRefreshToken_RevokesWithTheAccessToken()
    {
        // A row whose refresh token was never stored (a silent prior grant): the access token is the only
        // grant left, so that is what Google is asked to revoke.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(
            userId,
            refreshToken: null,
            ct: Ct);
        using var client = harness.ClientFor(userId);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        var revoke = harness.Google.RevokeRequests.ShouldHaveSingleItem();
        revoke.FormValue("token").ShouldBe(
            DisconnectChannelHarness.AccessToken,
            "the fallback is the access token, and it travels in the form body");

        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        using var data = JsonDocument.Parse(audit);
        data.RootElement.GetProperty("googleRevoke").GetString().ShouldBe("revoked");
    }

    /// <summary>Starts the harness, creating the flag row before the host exists.</summary>
    /// <param name="googleOptions">The fake's revoke answer.</param>
    /// <param name="flagOn">Whether <c>youtube_publish</c> is on.</param>
    private Task<DisconnectChannelHarness> StartAsync(FakeGoogleOptions? googleOptions = null, bool flagOn = true) =>
        DisconnectChannelHarness.StartAsync(Database, Ct, flagOn, googleOptions);

    /// <summary>Asserts the status and the problem body, and returns the body; a non-problem body fails here.</summary>
    /// <param name="response">The response to read.</param>
    /// <param name="expected">The status the route must have answered.</param>
    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)expected);

        return problem;
    }

    /// <summary>
    /// A <see cref="MeterListener"/> over <c>youtube_channel_revoke_failed_total</c>: what a scrape would see,
    /// started before the host so no instrument is published unobserved (09.9.3-T1's exporter is another
    /// listener).
    /// </summary>
    private sealed class RevokeFailureCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<long> _values = [];

        /// <summary>Starts the listener on the one instrument this test class asserts.</summary>
        internal RevokeFailureCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, NightTalesMetrics.MeterName, StringComparison.Ordinal)
                    && string.Equals(
                        instrument.Name,
                        NightTalesMetrics.YoutubeChannelRevokeFailedTotalName,
                        StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                lock (_values)
                {
                    _values.Add(value);
                }
            });

            _listener.Start();
        }

        /// <summary>How many measurements the instrument published.</summary>
        internal int Readings
        {
            get
            {
                lock (_values)
                {
                    return _values.Count;
                }
            }
        }

        /// <summary>The counter's total as this listener observed it.</summary>
        internal long Total
        {
            get
            {
                lock (_values)
                {
                    return _values.Sum();
                }
            }
        }

        /// <inheritdoc />
        public void Dispose() => _listener.Dispose();
    }

    /// <summary>An RFC 9457 problem body, as the route's refusals carry it.</summary>
    /// <param name="Code">The catalogued error code.</param>
    /// <param name="Status">The HTTP status the body repeats.</param>
    /// <param name="Errors">The per-field messages, when the refusal names any.</param>
    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
