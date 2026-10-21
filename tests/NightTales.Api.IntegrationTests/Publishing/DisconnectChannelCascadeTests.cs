using System.Net;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// What a <c>DELETE /api/v1/channels/{id}</c> cascades (06.4.1-T1 §Design steps 6-9, 06 README D10/D13/D17):
/// every <c>awaiting_consent</c>/<c>scheduled</c> publication of the channel is cancelled through the cancel
/// route's own step, the quota unit comes back <em>only</em> where the payload reserved one, the channel-scope
/// deletion request is scheduled seven days out, and neither the audit row, the outbox payload nor a log line
/// carries a token or a title.
/// </summary>
/// <remarks>
/// The route's own contract (204, the revoke, the wipe, the repeat and the two 404s) is asserted by
/// <see cref="DisconnectChannelTests"/>; both classes share <see cref="DisconnectChannelHarness"/> and run in
/// the <c>Process-global host state</c> collection, because one test there reads a process-wide
/// <c>MeterListener</c>.
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Collection("Process-global host state")]
[Trait("Category", "Integration")]
public sealed class DisconnectChannelCascadeTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>A payload without the quota key: the row never passed the reserve step.</summary>
    private const string PayloadWithoutQuotaKey = """{"uploadSessionUri":"https://upload.example.test/session/1"}""";

    /// <summary>
    /// A payload that names the key with a JSON <c>null</c>. S72-1 fixes the spelling: the writer removes the
    /// key instead of storing a null, so a null reads as "nothing reserved" and logs no warning.
    /// </summary>
    private const string PayloadWithNullQuotaKey = """{"quotaReservedDay":null}""";

    /// <summary>A key that is present but is not an ISO day.</summary>
    private const string PayloadWithUnparseableQuotaKey = """{"quotaReservedDay":"yesterday"}""";

    /// <summary>A key of the wrong JSON type, which is the same "present but unusable" case.</summary>
    private const string PayloadWithTypedQuotaKey = """{"quotaReservedDay":42}""";

    /// <summary>The D13 members of one publication change: ids, state and the error code only.</summary>
    private static readonly string[] PublicationPayloadMembers =
    [
        "publicationId", "episodeId", "userId", "status", "errorCode", "occurredAt",
    ];

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Disconnect_ReleasesQuotaOnlyWhenReserved()
    {
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);

        // The two open rows the cascade cancels: one that reserved a unit, one that never reached the
        // reserve step. The four later states are seeded with the same key to prove the release is driven by
        // the row's status and not by the payload alone.
        var reserved = await harness.SeedPublicationAsync(
            userId,
            channel.Id,
            platformPayloadJson: DisconnectChannelHarness.QuotaPayload(DisconnectChannelHarness.QuotaDay),
            ct: Ct);
        var withoutKey = await harness.SeedPublicationAsync(
            userId,
            channel.Id,
            platformPayloadJson: PayloadWithoutQuotaKey,
            ct: Ct);

        var processing = await harness.SeedPublicationAsync(
            userId,
            channel.Id,
            status: PublicationStatus.Processing,
            platformPayloadJson: DisconnectChannelHarness.QuotaPayload(DisconnectChannelHarness.QuotaDay),
            ct: Ct);
        var published = await harness.SeedPublicationAsync(
            userId,
            channel.Id,
            status: PublicationStatus.Published,
            platformPayloadJson: DisconnectChannelHarness.QuotaPayload(DisconnectChannelHarness.QuotaDay),
            ct: Ct);
        var failed = await harness.SeedPublicationAsync(
            userId,
            channel.Id,
            status: PublicationStatus.Failed,
            platformPayloadJson: DisconnectChannelHarness.QuotaPayload(DisconnectChannelHarness.QuotaDay),
            ct: Ct);
        var alreadyCancelled = await harness.SeedPublicationAsync(
            userId,
            channel.Id,
            status: PublicationStatus.Cancelled,
            platformPayloadJson: DisconnectChannelHarness.QuotaPayload(DisconnectChannelHarness.QuotaDay),
            ct: Ct);

        await harness.SeedQuotaRowAsync(DisconnectChannelHarness.QuotaDay, unitsUsed: 3, unitsLimit: 6, ct: Ct);
        await harness.SeedQuotaRowAsync(DisconnectChannelHarness.UntouchedQuotaDay, unitsUsed: 2, unitsLimit: 6, ct: Ct);

        var untouchedDayBefore = harness.QuotaRow(DisconnectChannelHarness.UntouchedQuotaDay);
        var laterStateXmins = new[] { processing, published, failed, alreadyCancelled }
            .ToDictionary(publication => publication.Id, publication => harness.Xmin(publication.Id));

        using var client = harness.ClientFor(userId);
        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        // Acceptance 7, second half: the two open rows are cancelled, the four later states are not touched.
        harness.PublicationStatusOf(reserved.Id).ShouldBe("cancelled");
        harness.PublicationStatusOf(withoutKey.Id).ShouldBe("cancelled");

        foreach (var (publicationId, xmin) in laterStateXmins)
        {
            harness.Xmin(publicationId).ShouldBe(
                xmin,
                "the cascade ignores the states that already moved on: the row is not written at all");
        }

        harness.PublicationStatusOf(processing.Id).ShouldBe("processing");
        harness.PublicationStatusOf(published.Id).ShouldBe("published");
        harness.PublicationStatusOf(failed.Id).ShouldBe("failed");
        harness.PublicationStatusOf(alreadyCancelled.Id).ShouldBe("cancelled");

        // Acceptance 7, first half: one unit back — the reserved row's, and only that one. A release for the
        // four rows that merely carry the key would take the counter to 0 (or below, which the ledger clamps).
        harness.UnitsUsed(DisconnectChannelHarness.QuotaDay).ShouldBe(
            2,
            "only the scheduled row with a reserved day gives its unit back (R16/invariant 4)");
        harness.QuotaRow(DisconnectChannelHarness.UntouchedQuotaDay).ShouldBe(
            untouchedDayBefore,
            "no other day's ledger row is touched");

        // D13: one publication_updated.v1 per cancelled row and none for the four later states.
        harness.PublicationPayloads(reserved.Id).ShouldHaveSingleItem();
        harness.PublicationPayloads(withoutKey.Id).ShouldHaveSingleItem();
        foreach (var publicationId in laterStateXmins.Keys)
        {
            harness.PublicationPayloads(publicationId).ShouldBeEmpty();
        }

        // D17: the disconnect's own audit row reports both counts.
        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        using (var data = JsonDocument.Parse(audit))
        {
            data.RootElement.GetProperty("publicationsCancelled").GetInt32().ShouldBe(
                2,
                "the cascade cancelled the two open rows and nothing else");
            data.RootElement.GetProperty("quotaUnitsReleased").GetInt32().ShouldBe(
                1,
                "the count the ledger moved by, not the number of cancelled rows");
        }
    }

    [Fact]
    public async Task Disconnect_CreatesChannelScopeRequest_DueIn7Days()
    {
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        using var client = harness.ClientFor(userId);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        // Acceptance 6: the engine's own row — scope, target, reason and the 7-day deadline, all on the
        // command's single instant.
        harness.DeletionRequests(userId, channel.Id).ShouldHaveSingleItem();
        harness.DeletionHas(userId, channel.Id, "scope = 'channel'")
            .ShouldBeTrue("a disconnect purges the channel's data, not the account's");
        harness.DeletionHas(userId, channel.Id, "reason = 'in_app_revoke'")
            .ShouldBeTrue("the owner revoked the grant in the app, which is its own deadline class (13 §5.4)");
        harness.DeletionHas(userId, channel.Id, "status = 'pending'")
            .ShouldBeTrue("a freshly scheduled request is pending");
        harness.DeletionHas(userId, channel.Id, "completed_at IS NULL")
            .ShouldBeTrue("nothing has been purged yet");
        harness.DeletionHas(
                userId,
                channel.Id,
                $"requested_at = {DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start)}")
            .ShouldBeTrue("requested_at is the disconnect instant, not the row's creation");
        harness.DeletionHas(
                userId,
                channel.Id,
                $"due_at = {DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start.AddDays(7))}")
            .ShouldBeTrue("due_at = requested_at + 7 d exactly, computed by the engine and never by the handler");
        harness.DeletionHas(userId, channel.Id, "due_at - requested_at = interval '7 days'")
            .ShouldBeTrue("the deadline invariant holds in the database itself");
    }

    [Fact]
    public async Task Disconnect_QuotaKeyEdgeCases_ReleaseNothingAndWarnOnlyWhenUnreadable()
    {
        // The three payload shapes the release rule distinguishes (06.2.6-T1 §Design step 5): absent, a JSON
        // null (absent per S72-1) and present-but-unusable (one warning line per row, never a failure).
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);

        var absent = await harness.SeedPublicationAsync(
            userId, channel.Id, platformPayloadJson: PayloadWithoutQuotaKey, ct: Ct);
        var jsonNull = await harness.SeedPublicationAsync(
            userId, channel.Id, platformPayloadJson: PayloadWithNullQuotaKey, ct: Ct);
        var unparseable = await harness.SeedPublicationAsync(
            userId, channel.Id, platformPayloadJson: PayloadWithUnparseableQuotaKey, ct: Ct);
        var wrongType = await harness.SeedPublicationAsync(
            userId, channel.Id, platformPayloadJson: PayloadWithTypedQuotaKey, ct: Ct);

        await harness.SeedQuotaRowAsync(DisconnectChannelHarness.QuotaDay, unitsUsed: 1, unitsLimit: 6, ct: Ct);
        var ledgerBefore = harness.QuotaRow(DisconnectChannelHarness.QuotaDay);
        var ledgerRowsBefore = harness.QuotaRowCount(DisconnectChannelHarness.QuotaDay);

        using var client = harness.ClientFor(userId);
        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "a payload nobody can read never fails the disconnect (task §Failure handling): "
            + await response.Content.ReadAsStringAsync(Ct));

        // All four rows are cancelled — the cascade is about the status, not the payload.
        foreach (var publication in new[] { absent, jsonNull, unparseable, wrongType })
        {
            harness.PublicationStatusOf(publication.Id).ShouldBe("cancelled");
        }

        // None of them releases: the ledger row is byte-for-byte the one it was, and no row is ever created.
        harness.QuotaRow(DisconnectChannelHarness.QuotaDay).ShouldBe(
            ledgerBefore,
            "nothing reserved means nothing to release; an over-eager release would take units_used to 0");
        harness.QuotaRowCount(DisconnectChannelHarness.QuotaDay).ShouldBe(ledgerRowsBefore);

        // Exactly one 6505 line per unusable row, and none for the two "absent" shapes.
        harness.Logs.CountOf(DisconnectChannelHarness.ReservedDayUnreadableEventId).ShouldBe(
            2,
            "a JSON null is absent and costs no warning; a malformed or wrongly-typed key warns once per row");

        var warnings = harness.Logs.Lines
            .Where(line => line.EventId == DisconnectChannelHarness.ReservedDayUnreadableEventId)
            .Select(line => line.Message)
            .ToList();

        warnings.ShouldContain(message => message.Contains($"{unparseable.Id}", StringComparison.Ordinal));
        warnings.ShouldContain(message => message.Contains($"{wrongType.Id}", StringComparison.Ordinal));
        warnings.ShouldNotContain(message => message.Contains($"{absent.Id}", StringComparison.Ordinal));
        warnings.ShouldNotContain(message => message.Contains($"{jsonNull.Id}", StringComparison.Ordinal));
        warnings.ShouldNotContain(message => message.Contains("yesterday", StringComparison.Ordinal));

        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        using var data = JsonDocument.Parse(audit);
        data.RootElement.GetProperty("quotaUnitsReleased").GetInt32().ShouldBe(0);
        data.RootElement.GetProperty("publicationsCancelled").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task Disconnect_AuditOutboxAndLogs_CarryNoTokenAndNoTitles()
    {
        // Acceptance 10: the whole written surface of one disconnect, with a canary in every field the payload
        // and the audit could leak — the two plaintext tokens, the channel title and the publication's text.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, displayName: DisconnectChannelHarness.ChannelTitle, ct: Ct);
        var publication = await harness.SeedPublicationAsync(userId, channel.Id, ct: Ct);

        using var client = harness.ClientFor(userId);
        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        var connectionPayload = harness.ConnectionPayloads(channel.Id).ShouldHaveSingleItem();
        var publicationPayload = harness.PublicationPayloads(publication.Id).ShouldHaveSingleItem();
        var logs = harness.Logs.AllText();
        logs.ShouldNotBeNullOrWhiteSpace("the capture must have seen this host's own lines, or the search proves nothing");

        // Ids, counts and codes only: the audit's member set is frozen by 06 README D17.
        using (var data = JsonDocument.Parse(audit))
        {
            var names = data.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            names.ShouldContain("platform");
            names.ShouldContain("oauthClientId");
            names.ShouldContain("externalAccountId");
            names.ShouldContain("publicationsCancelled");
            names.ShouldContain("quotaUnitsReleased");
            names.ShouldContain("googleRevoke");
            names.ShouldBeSubsetOf(
                [
                    "channelId", "platform", "oauthClientId", "externalAccountId",
                    "publicationsCancelled", "quotaUnitsReleased", "googleRevoke",
                ],
                "an extra member would be a second copy of user text in an append-only table");
        }

        using (var payload = JsonDocument.Parse(publicationPayload))
        {
            payload.RootElement.EnumerateObject().Select(property => property.Name).Order().ShouldBe(
                PublicationPayloadMembers.Order(),
                ignoreOrder: true,
                customMessage: "D13 fixes the publication payload's member set");
        }

        foreach (var document in new[] { audit, connectionPayload, publicationPayload, logs })
        {
            document.ShouldNotContain(DisconnectChannelHarness.AccessToken, Case.Sensitive);
            document.ShouldNotContain(DisconnectChannelHarness.RefreshToken, Case.Sensitive);
            document.ShouldNotContain(DisconnectChannelHarness.ChannelTitle, Case.Sensitive);
            document.ShouldNotContain(DisconnectChannelHarness.PublicationTitle, Case.Sensitive);
            document.ShouldNotContain(DisconnectChannelHarness.PublicationDescription, Case.Sensitive);
        }
    }

    [Fact]
    public async Task Disconnect_Cascade_LeavesTheCancelAuditToTheCancelRoute()
    {
        // STATUS S72-4's actor parameter is the step's, not a written row: the disconnect cancels through
        // PublicationCancelStep with actor = system:disconnect, and it writes no publishing.cancelled row of
        // its own. What must hold either way is that no cancel of this cascade is attributed to the user.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var publication = await harness.SeedPublicationAsync(userId, channel.Id, ct: Ct);
        using var client = harness.ClientFor(userId);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        harness.PublicationStatusOf(publication.Id).ShouldBe("cancelled");
        harness.PublicationAuditCount(publication.Id).ShouldBe(
            0,
            "D17's publishing.cancelled row stays the cancel route's: a disconnect writes one audit row, its own");

        harness.AuditData(channel.Id)
            .ShouldHaveSingleItem()
            .ShouldNotContain("\"actor\"", Case.Sensitive);

        using var payload = JsonDocument.Parse(harness.PublicationPayloads(publication.Id).ShouldHaveSingleItem());
        payload.RootElement.TryGetProperty("actor", out _).ShouldBeFalse(
            "the D13 payload carries the ids, the state and the error code — no actor at all");
    }

    /// <summary>Starts the harness, creating the flag row before the host exists.</summary>
    private Task<DisconnectChannelHarness> StartAsync() =>
        DisconnectChannelHarness.StartAsync(Database, Ct);
}
