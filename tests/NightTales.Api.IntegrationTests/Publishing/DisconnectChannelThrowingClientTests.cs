using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Telemetry;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The two shapes of a <see cref="IYouTubeClient.RevokeAsync"/> that <em>throws</em> instead of answering, over the
/// real Api and a real PostgreSQL 17 (06.4.1-T1 §Failure handling, R10): a non-cancellation exception is a final
/// failed revoke — one attempt, no retry, <c>googleRevoke=failed</c>, one
/// <c>youtube_channel_revoke_failed_total</c> and one 6509 naming the exception's type, then the usual wipe and
/// <c>204</c>, with neither the token nor the title of the row anywhere it is written — while the caller's own
/// cancellation propagates, rolls the unit of work back and leaves the row exactly as it was.
/// </summary>
/// <remarks>
/// <para>
/// The port is replaced per test: <see cref="DisconnectChannelHarness.StartAsync"/> takes one extra registration pass
/// over this test's own host, so the production <c>GoogleYouTubeClient</c> is removed and
/// <see cref="ThrowingYouTubeClient"/> is registered in its place. Nothing else about the host moves: the same
/// handler, transaction, audit and outbox run, and the fake Google endpoints recording <em>no</em> call is itself
/// evidence that the replaced port served the request.
/// </para>
/// <para>
/// The class joins the <c>Process-global host state</c> collection for the same reason
/// <see cref="DisconnectChannelTests"/> does: the counter assertions read a process-wide
/// <see cref="MeterListener"/> of the <c>NightTales</c> meter, so no second host of this collection may publish while
/// one of these tests measures.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Collection("Process-global host state")]
[Trait("Category", "Integration")]
public sealed class DisconnectChannelThrowingClientTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    /// <summary>
    /// The title the seeded row stores <em>and</em> the throwing client repeats in its message: a value that lives in
    /// the row and in the exception, so one comparison catches a leak of either.
    /// </summary>
    private const string CanaryTitle = "Kênh canary S73 ném lỗi";

    [Fact]
    public async Task Disconnect_RevokeClientThrows_StillWipesAndRecordsFailed()
    {
        // The client throws with the plaintext token and the channel's title inside the message: the handler must
        // record a final failed revoke, wipe the row and answer 204 — and the message must never leave the method.
        using var counter = new RevokeFailureCounter();
        var throwing = new ThrowingYouTubeClient(
            token => new InvalidOperationException($"the revoke of {token} for '{CanaryTitle}' was refused"));
        await using var harness = await StartAsync(throwing);
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, displayName: CanaryTitle, ct: Ct);
        using var client = harness.ClientFor(userId);

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        // R10: a revoke that throws is not the owner's problem; the disconnect happens anyway.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, text);
        text.ShouldBeEmpty("a 204 carries no body");

        // One attempt and no retry: a non-cancellation exception is not the transient `service_unavailable` class.
        throwing.RevokeTokens.ShouldHaveSingleItem().ShouldBe(
            DisconnectChannelHarness.RefreshToken,
            "the refresh token is the grant the ladder revokes, and a thrown revoke is asked for exactly once");
        harness.Google.Calls.ShouldBeEmpty("the replaced port never reaches the fake Google endpoints");

        harness.ChannelHas(channel.Id, "status = 'revoked' AND access_token_enc IS NULL AND refresh_token_enc IS NULL")
            .ShouldBeTrue("a thrown revoke still wipes both ciphertexts and revokes the row (D15/R10)");

        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        using (var data = JsonDocument.Parse(audit))
        {
            data.RootElement.GetProperty("googleRevoke").GetString().ShouldBe(
                "failed",
                "an exception is a failed revoke, never a skipped or a revoked one");
        }

        counter.Readings.ShouldBe(
            1,
            "youtube_channel_revoke_failed_total is incremented once for the failed disconnect, not once per attempt");
        counter.Total.ShouldBe(1);

        // 6509 carries the exception's type as the error code — never its message, which holds the token.
        harness.Logs.CountOf(DisconnectChannelHarness.RevokeFailedEventId)
            .ShouldBe(1, "the failed revoke is logged once, after the single attempt");
        harness.Logs.Lines.ShouldContain(
            line => line.EventId == DisconnectChannelHarness.RevokeFailedEventId
                && line.Message.Contains("InvalidOperationException", StringComparison.Ordinal),
            "the failed line reports the exception type as its error code");

        harness.DeletionRequestCount(userId)
            .ShouldBe(1, "the channel-scope purge is scheduled whatever Google answered");

        // D17: neither the token nor the title of the row travels into a log line, the audit row or the outbox row —
        // the exception message must never leak through any of them.
        var logs = harness.Logs.AllText();
        var auditRow = SqlProbe.QueryStrings(
                harness.Database,
                "SELECT to_jsonb(t)::text FROM audit_logs AS t "
                + $"WHERE t.action = '{DisconnectChannelHarness.AuditAction}' AND t.entity_id = '{channel.Id}'")
            .ShouldHaveSingleItem();
        var outboxRow = SqlProbe.QueryStrings(
                harness.Database,
                "SELECT to_jsonb(t)::text FROM outbox_messages AS t "
                + $"WHERE t.type = '{DisconnectChannelHarness.ConnectionEventType}' "
                + $"AND t.payload->>'connectionId' = '{channel.Id}'")
            .ShouldHaveSingleItem();

        string[] secrets =
        [
            DisconnectChannelHarness.RefreshToken,
            DisconnectChannelHarness.AccessToken,
            CanaryTitle,
        ];

        foreach (var secret in secrets)
        {
            logs.ShouldNotContain(secret, Case.Sensitive);
            auditRow.ShouldNotContain(secret, Case.Sensitive);
            outboxRow.ShouldNotContain(secret, Case.Sensitive);
        }
    }

    [Fact]
    public async Task Disconnect_RevokeClientCancelled_PropagatesAndWritesNothing()
    {
        // The client answers the caller's own cancellation instead of an outcome: the ladder may not turn that into
        // a failed revoke, so it propagates, the unit of work rolls back and the row keeps its tokens — the owner can
        // simply retry.
        using var counter = new RevokeFailureCounter();
        var throwing = new ThrowingYouTubeClient(
            _ => new OperationCanceledException("the client stopped listening to the revoke"));
        await using var harness = await StartAsync(throwing);
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        using var client = harness.ClientFor(userId);

        var rowBefore = harness.ChannelRow(channel.Id);

        // The request is built here, not through the helper, so it stays alive for the whole call exactly as
        // DisconnectChannelQaTests does on its aborted-request case.
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{DisconnectChannelHarness.ChannelsRoute}/{channel.Id}");

        HttpResponseMessage? answered = null;
        Exception? failure = null;

        try
        {
            answered = await client.SendAsync(request, Ct);
        }
        catch (Exception exception)
        {
            // The caller either observes its own cancellation or — if the host's exception handler ran first — a
            // server-side answer. Both are legal here; what must never happen is a 204 for a disconnect that wrote
            // nothing.
            failure = exception;
        }

        if (answered is null)
        {
            Unwrap(failure).ShouldBeAssignableTo<OperationCanceledException>(
                "the client's cancellation surfaces to the caller instead of being answered as a disconnect");
        }
        else
        {
            using (answered)
            {
                answered.StatusCode.ShouldNotBe(
                    HttpStatusCode.NoContent,
                    "a cancelled revoke rolls the whole disconnect back, so it can never answer the 204 of a "
                    + "completed one: " + await answered.Content.ReadAsStringAsync(Ct));
            }
        }

        harness.ChannelRow(channel.Id).ShouldBe(
            rowBefore,
            "the rollback leaves the row exactly as it was: active, both ciphers present, revoked_at untouched");
        harness.ChannelHas(channel.Id, "status = 'active' AND access_token_enc IS NOT NULL AND refresh_token_enc IS NOT NULL")
            .ShouldBeTrue("a cancelled revoke must never wipe the tokens it could not revoke");
        harness.AuditData(channel.Id).ShouldBeEmpty("no audit row for a rolled-back command");
        harness.ConnectionPayloads(channel.Id).ShouldBeEmpty("no outbox row for a rolled-back command");
        harness.DeletionRequestCount(userId).ShouldBe(0, "no purge is scheduled for a rolled-back command");

        throwing.RevokeTokens.ShouldHaveSingleItem(
            "the cancellation is not retried: the first attempt is the one that propagated");
        counter.Readings.ShouldBe(
            0,
            "a cancelled revoke is not a failed one: youtube_channel_revoke_failed_total must not move");
        harness.Google.Calls.ShouldBeEmpty("the replaced port never reaches the fake Google endpoints");
    }

    /// <summary>Starts the harness whose host resolves <paramref name="client"/> in place of the production port.</summary>
    /// <param name="client">The fake the request's scope must resolve as its <see cref="IYouTubeClient"/>.</param>
    private Task<DisconnectChannelHarness> StartAsync(ThrowingYouTubeClient client) =>
        DisconnectChannelHarness.StartAsync(
            Database,
            Ct,
            services: services =>
            {
                // The production registration is a singleton (AddYouTubePublishing), so the fake takes the same
                // lifetime: one instance per host, which is how this test counts the attempts it saw.
                services.RemoveAll<IYouTubeClient>();
                services.AddSingleton<IYouTubeClient>(client);
            });

    /// <summary>Peels the wrappers a host's request pipeline may add, so the interesting failure is compared.</summary>
    /// <param name="failure">The exception the client observed, or null.</param>
    /// <returns>The innermost exception of a single-failure aggregate, or <paramref name="failure"/> itself.</returns>
    private static Exception? Unwrap(Exception? failure)
    {
        while (failure is AggregateException { InnerException: { } inner })
        {
            failure = inner;
        }

        return failure;
    }

    /// <summary>
    /// The revoke port, replaced for one test: it records every token it is handed and throws the exception its
    /// <paramref name="failure"/> builds, never answering an <see cref="ErrorOr{T}"/>.
    /// </summary>
    /// <param name="failure">Builds the exception from the token the handler handed over.</param>
    private sealed class ThrowingYouTubeClient(Func<string, Exception> failure) : IYouTubeClient
    {
        /// <summary>Every token handed to <see cref="RevokeAsync"/>, in arrival order.</summary>
        internal List<string> RevokeTokens { get; } = [];

        public Task<ErrorOr<RevokeOutcome>> RevokeAsync(string token, CancellationToken ct)
        {
            RevokeTokens.Add(token);

            throw failure(token);
        }

        public string BuildAuthorizationUrl(
            OAuthClient client,
            OAuthAuthorizationRequest request,
            string state,
            string? codeChallenge) =>
            throw new NotSupportedException("The disconnect never builds an authorization URL.");

        public Task<ErrorOr<OAuthTokens>> ExchangeCodeAsync(
            OAuthClient client,
            string code,
            string? codeVerifier,
            string redirectUri,
            CancellationToken ct) =>
            throw new NotSupportedException("The disconnect never exchanges a code.");

        public Task<ErrorOr<OAuthTokens>> RefreshAsync(OAuthClient client, string refreshToken, CancellationToken ct) =>
            throw new NotSupportedException("The disconnect never refreshes a token.");

        public Task<ErrorOr<GoogleChannel>> GetMyChannelAsync(string accessToken, CancellationToken ct) =>
            throw new NotSupportedException("The disconnect never reads the channel.");

        public Task<ErrorOr<Uri>> StartUploadAsync(
            string accessToken,
            VideoInsertRequest request,
            long length,
            CancellationToken ct) =>
            throw new NotSupportedException("The disconnect never starts an upload.");

        public Task<ErrorOr<UploadResult>> ResumeUploadAsync(
            string accessToken,
            Uri sessionUri,
            Stream content,
            long length,
            IProgress<long>? progress,
            CancellationToken ct) =>
            throw new NotSupportedException("The disconnect never resumes an upload.");

        public Task<ErrorOr<UploadProbe>> ProbeUploadAsync(
            string accessToken,
            Uri sessionUri,
            long length,
            CancellationToken ct) =>
            throw new NotSupportedException("The disconnect never probes an upload.");

        public Task<ErrorOr<IReadOnlyList<YouTubeVideoStatus>>> GetVideosAsync(
            string accessToken,
            IReadOnlyList<string> ids,
            CancellationToken ct) =>
            throw new NotSupportedException("The disconnect never reads video statuses.");
    }

    /// <summary>
    /// A <see cref="MeterListener"/> over <c>youtube_channel_revoke_failed_total</c>: what a scrape would see,
    /// started before the host so no instrument is published unobserved (the same reader
    /// <see cref="DisconnectChannelTests"/> uses).
    /// </summary>
    private sealed class RevokeFailureCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<long> _values = [];

        /// <summary>Starts the listener on the one instrument these tests assert.</summary>
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
}
