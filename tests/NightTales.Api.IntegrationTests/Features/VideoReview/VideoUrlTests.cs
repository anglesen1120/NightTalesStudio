using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Episodes;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.Infrastructure.Storage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.VideoReview;

/// <summary>
/// <c>GET /api/v1/episodes/{id}/video-url</c> (09.7.2-T1 acceptance): the presigned URL, its 15-minute
/// ceiling, the safe download header, the audit row of its own transaction, and the 404/503 answers.
/// </summary>
/// <remarks>
/// <para>
/// The store is <see cref="InMemoryBlobStore"/> (09 README D13): the real S3 implementation is exercised
/// by the <c>Storage</c>-category tests against the dev container, and these tests are about the route's
/// contract. The clock is the <see cref="FakeClock"/> the Api host resolves, so the expiry is exact.
/// </para>
/// <para>
/// The audit assertions read <c>audit_logs</c> through Npgsql rather than through the audit writer's own
/// types: the row is cast to text (<c>audit_logs::text</c>), which keeps the assertion independent of
/// the column names the audit task owns while still failing if a URL or a signature ever lands in it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class VideoUrlTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The fixed instant the host's clock reports, so the expiry is deterministic.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The route under test; spelled out because a path is a contract (09 README D14).</summary>
    private const string VideoUrlRoute = "/api/v1/episodes";

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task VideoUrl_ReturnsUrlAndExpiresAt_FifteenMinutesApart()
    {
        var userId = await CreateUserAsync();
        var (episodeId, assetId, store) = await SeedEpisodeWithVideoAsync(userId, title: "Tập một");
        using var factory = Host(store);
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var body = await response.Content.ReadFromJsonAsync<VideoUrlBody>(Ct);
        body.ShouldNotBeNull();
        body.Url.ShouldContain("X-Amz-Signature", Case.Sensitive, "the URL must be a real presign");
        body.ExpiresAt.ShouldBe(Start.AddMinutes(15), "the URL lives 15 minutes (WF16)");

        var request = store.ReadUrlRequests.ShouldHaveSingleItem();
        request.Bucket.ShouldBe("nt-media-dev");
        request.Key.ShouldBe(await ReadStorageKeyAsync(userId, assetId));
        request.Ttl.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task VideoUrl_ClampsTheConfiguredTtlToFifteenMinutes()
    {
        var userId = await CreateUserAsync();
        var (episodeId, _, store) = await SeedEpisodeWithVideoAsync(userId, title: "Tập một");
        using var factory = Host(store).ConfigureServices(
            services => services.Configure<StorageSettings>(settings => settings.PresignTtlMinutes = 60));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var body = await response.Content.ReadFromJsonAsync<VideoUrlBody>(Ct);
        body.ShouldNotBeNull();
        body.ExpiresAt.ShouldBe(Start.AddMinutes(15), "WF16's ceiling wins over a longer configured value");
        store.ReadUrlRequests.ShouldHaveSingleItem().Ttl.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task VideoUrl_ContentDisposition_IsAsciiFoldedAttachmentWithMp4Suffix()
    {
        var userId = await CreateUserAsync();
        var (episodeId, _, store) = await SeedEpisodeWithVideoAsync(userId, title: "Tập một: Chuyện của Đại");
        using var factory = Host(store);
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var request = store.ReadUrlRequests.ShouldHaveSingleItem();
        request.DownloadFileName.ShouldBe("tap-mot-chuyen-cua-dai.mp4", "the slug of the episode title");

        // The store turns that name into the header of the presigned response; the header is what a
        // browser sees, so it is what must be safe (BD16).
        var header = SafeContentDisposition.Attachment(request.DownloadFileName);
        header.ShouldNotBeNull();

        var (ascii, extended) = FilenameParameters(header);

        ascii.ShouldBe("tap-mot-chuyen-cua-dai.mp4");
        ascii.ShouldEndWith(".mp4");
        extended.ShouldEndWith(".mp4");

        foreach (var parameter in new[] { ascii, extended })
        {
            parameter.ShouldNotContain("\"", Case.Sensitive);
            parameter.ShouldNotContain("\\", Case.Sensitive);
            parameter.ShouldNotContain("\r", Case.Sensitive);
            parameter.ShouldNotContain("\n", Case.Sensitive);
            parameter.All(char.IsAscii).ShouldBeTrue($"'{parameter}' must be ASCII");
        }
    }

    [Fact]
    public async Task VideoUrl_NeverWritesTheUrlToLogsOrAudit()
    {
        var userId = await CreateUserAsync();
        var (episodeId, _, store) = await SeedEpisodeWithVideoAsync(userId, title: "Tập một");
        var sink = new CapturingLoggerProvider();
        using var factory = Host(store)
            .ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(sink)));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var logs = sink.Text;

        // Non-vacuous: the request really was logged through this sink.
        logs.ShouldContain("video-url", Case.Sensitive, "the request line of this route proves the sink is live");
        logs.ShouldNotContain("X-Amz-", Case.Sensitive, "a signature must never be logged (BD16)");
        logs.ShouldNotContain("storage.test", Case.Sensitive);

        var rows = await ReadAuditRowsAsync(episodeId);

        var row = rows.ShouldHaveSingleItem();
        row.ShouldContain("episode.video_url_issued");
        row.ShouldNotContain("X-Amz-", Case.Sensitive, "the audit payload carries ids only");
        row.ShouldNotContain("http", Case.Sensitive, "no URL, not even a scheme, may be audited");
    }

    [Fact]
    public async Task VideoUrl_WritesEpisodeVideoUrlIssuedAuditRow_InItsOwnTransaction()
    {
        var userId = await CreateUserAsync();
        var (episodeId, assetId, store) = await SeedEpisodeWithVideoAsync(userId, title: "Tập một");
        var probe = new UnitOfWorkProbe();
        using var factory = Host(store)
            .ConfigureServices(services => UseRecordingUnitOfWork(services, probe, failCommit: false));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        // A GET has no Mediator pipeline, so the endpoint itself must have opened exactly one
        // transaction around the audit insert (the writer throws without an ambient one).
        probe.BeginCalls.ShouldBe(1);
        probe.CommitCalls.ShouldBe(1);

        var row = (await ReadAuditRowsAsync(episodeId)).ShouldHaveSingleItem();
        row.ShouldContain("episode.video_url_issued");
        row.ShouldContain(episodeId.ToString());
        row.ShouldContain(assetId.ToString());
        row.ShouldContain(userId.ToString(), Case.Sensitive, "the subject of a user-visible row is always set");
    }

    [Fact]
    public async Task VideoUrl_WhenAuditCommitFails_Returns503()
    {
        var userId = await CreateUserAsync();
        var (episodeId, _, store) = await SeedEpisodeWithVideoAsync(userId, title: "Tập một");
        var probe = new UnitOfWorkProbe();
        using var factory = Host(store)
            .ConfigureServices(services => UseRecordingUnitOfWork(services, probe, failCommit: true));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ReadProblemAsync(response)).Code.ShouldBe(ErrorCodes.ServiceUnavailable);

        // No URL left the Api, and nothing was written: an unaudited presigned URL must not exist.
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("X-Amz-", Case.Sensitive);
        probe.CommitCalls.ShouldBe(1);
        (await ReadAuditRowsAsync(episodeId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task VideoUrl_WhenBlobStoreFails_Returns503ServiceUnavailable()
    {
        var userId = await CreateUserAsync();
        var (episodeId, _, store) = await SeedEpisodeWithVideoAsync(userId, title: "Tập một");
        store.ReadUrlFailure = Error.Failure(ErrorCodes.ServiceUnavailable, "test presign failure");
        using var factory = Host(store);
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ReadProblemAsync(response)).Code.ShouldBe(ErrorCodes.ServiceUnavailable);

        // The failure happens before the audit transaction opens.
        (await ReadAuditRowsAsync(episodeId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task VideoUrl_WithoutFinalVideoAsset_Returns404()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = (await Episodes(userId).CreateAsync(userId, seriesId, status: EpisodeStatus.VideoReview, ct: Ct)).Id;
        var store = new InMemoryBlobStore();
        using var factory = Host(store);
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response)).Code.ShouldBe(ErrorCodes.NotFound);
        store.ReadUrlRequests.ShouldBeEmpty("nothing is signed for an episode with no final video");
    }

    [Fact]
    public async Task VideoUrl_OfAnotherUsersEpisode_Returns404()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var (episodeId, _, store) = await SeedEpisodeWithVideoAsync(owner, title: "Tập một");
        using var factory = Host(store);
        using var client = factory.ClientFor(stranger);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a foreign episode is 404, never 403 (D10)");
        (await ReadProblemAsync(response)).Code.ShouldBe(ErrorCodes.NotFound);
        (await ReadAuditRowsAsync(episodeId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task VideoUrl_WhenAssetDeleted_Returns404()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = (await Episodes(userId).CreateAsync(userId, seriesId, status: EpisodeStatus.VideoReview, ct: Ct)).Id;
        var asset = await Episodes(userId).CreateAssetAsync(
            userId,
            episodeId,
            deletedAt: Start.AddMinutes(-1),
            ct: Ct);
        await Episodes(userId).AttachFinalVideoAsync(userId, episodeId, asset.Id, Ct);
        var store = new InMemoryBlobStore();
        using var factory = Host(store);
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the sweep already deleted the object");
        (await ReadProblemAsync(response)).Code.ShouldBe(ErrorCodes.NotFound);
        store.ReadUrlRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task VideoUrl_Anonymous_Returns401()
    {
        var userId = await CreateUserAsync();
        var (episodeId, _, store) = await SeedEpisodeWithVideoAsync(userId, title: "Tập một");
        using var factory = Host(store);
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        store.ReadUrlRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task VideoUrl_WithHostileTitle_StillSignsASafeDownloadName()
    {
        var userId = await CreateUserAsync();

        // A title is user-controlled text: path segments, quotes and a CR/LF pair flow through the
        // slug into the signed Content-Disposition. None of it may reach the filename the browser
        // saves (WF16/BD16).
        var (episodeId, _, store) = await SeedEpisodeWithVideoAsync(
            userId, title: "../../evil\"\r\ninjected.mp4");
        using var factory = Host(store);
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(VideoUrl(episodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var request = store.ReadUrlRequests.ShouldHaveSingleItem();
        request.DownloadFileName.ShouldEndWith(".mp4");
        request.DownloadFileName.ShouldNotContain("/");
        request.DownloadFileName.ShouldNotContain("\\");
        request.DownloadFileName.ShouldNotContain("\"");
        request.DownloadFileName.ShouldNotContain("\r");
        request.DownloadFileName.ShouldNotContain("\n");
        request.DownloadFileName.ShouldNotContain("..");

        var header = SafeContentDisposition.Attachment(request.DownloadFileName);
        header.ShouldNotBeNull();
        header.ShouldNotContain("\r");
        header.ShouldNotContain("\n");
    }

    private static string VideoUrl(Guid episodeId) => $"{VideoUrlRoute}/{episodeId}/video-url";

    /// <summary>The host with the recording blob store in place of the S3 one.</summary>
    private ApiFactory Host(InMemoryBlobStore store) =>
        new ApiFactory(Database, new FakeClock(Start)).ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(store);
        });

    /// <summary>
    /// Replaces the unit of work with a decorator that counts the calls and can refuse the commit, which
    /// is how the "own transaction" and "audit commit fails" legs are observed without a mocking library.
    /// </summary>
    private static void UseRecordingUnitOfWork(IServiceCollection services, UnitOfWorkProbe probe, bool failCommit)
    {
        services.AddSingleton(probe);
        services.RemoveAll<IUnitOfWork>();
        services.AddScoped<IUnitOfWork>(provider => new RecordingUnitOfWork(
            new EfUnitOfWork(provider.GetRequiredService<AppDbContext>()),
            provider.GetRequiredService<UnitOfWorkProbe>(),
            failCommit));
    }

    private async Task<Guid> CreateUserAsync()
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;

    private SeriesBuilder Series() => new(Database, new TestCurrentUser(), new FakeClock(Start));

    private EpisodeBuilder Episodes(Guid userId) =>
        new(Database, new TestCurrentUser { Id = userId }, new FakeClock(Start));

    private async Task<Guid> SeedSeriesAsync(Guid userId)
    {
        var voice = await Series().EnsureVoiceAsync(ct: Ct);
        var template = await Series().EnsureStyleTemplateAsync(ct: Ct);

        var series = await Series().CreateAsync(
            userId,
            title: "Chuỗi thử nghiệm",
            voiceId: voice.Id,
            styleTemplateId: template.Id,
            ct: Ct);

        return series.Id;
    }

    /// <summary>
    /// Seeds an episode in <c>video_review</c> with a live final video asset and the pointer between
    /// them, which is the state the route serves.
    /// </summary>
    private async Task<(Guid EpisodeId, Guid AssetId, InMemoryBlobStore Store)> SeedEpisodeWithVideoAsync(
        Guid userId,
        string title)
    {
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = (await Episodes(userId).CreateAsync(
            userId,
            seriesId,
            title: title,
            status: EpisodeStatus.VideoReview,
            ct: Ct)).Id;

        var asset = await Episodes(userId).CreateAssetAsync(userId, episodeId, ct: Ct);
        await Episodes(userId).AttachFinalVideoAsync(userId, episodeId, asset.Id, Ct);

        return (episodeId, asset.Id, new InMemoryBlobStore());
    }

    private async Task<string> ReadStorageKeyAsync(Guid userId, Guid assetId)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });

        return (await context.Assets.SingleAsync(asset => asset.Id == assetId, Ct)).StorageKey;
    }

    /// <summary>Every <c>audit_logs</c> row of the episode, as Postgres renders the whole row.</summary>
    private async Task<IReadOnlyList<string>> ReadAuditRowsAsync(Guid episodeId)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(
            "SELECT audit_logs::text FROM audit_logs WHERE entity_type = 'episode' AND entity_id = $1",
            connection);

        command.Parameters.AddWithValue(episodeId);

        var rows = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Ct);

        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)response.StatusCode);

        return problem;
    }

    private static (string Ascii, string Extended) FilenameParameters(string disposition)
    {
        var asciiStart = disposition.IndexOf('"', StringComparison.Ordinal) + 1;
        var asciiEnd = disposition.IndexOf('"', asciiStart);
        var extendedStart = disposition.IndexOf("UTF-8''", StringComparison.Ordinal) + "UTF-8''".Length;

        return (disposition[asciiStart..asciiEnd], disposition[extendedStart..]);
    }

    /// <summary>The counters of <see cref="RecordingUnitOfWork"/>.</summary>
    private sealed class UnitOfWorkProbe
    {
        /// <summary>How many transactions the route opened.</summary>
        public int BeginCalls { get; set; }

        /// <summary>How many commits the route asked for.</summary>
        public int CommitCalls { get; set; }
    }

    /// <summary>The real unit of work, with the two counters and an optional refused commit.</summary>
    private sealed class RecordingUnitOfWork(IUnitOfWork inner, UnitOfWorkProbe probe, bool failCommit) : IUnitOfWork
    {
        public Task BeginAsync(CancellationToken cancellationToken)
        {
            probe.BeginCalls++;

            return inner.BeginAsync(cancellationToken);
        }

        public async Task<ErrorOr<Success>> CommitAsync(CancellationToken cancellationToken)
        {
            probe.CommitCalls++;

            if (!failCommit)
            {
                return await inner.CommitAsync(cancellationToken);
            }

            await inner.RollbackAsync(cancellationToken);

            return Error.Failure(ErrorCodes.ServiceUnavailable, "test commit failure");
        }

        public Task RollbackAsync(CancellationToken cancellationToken) => inner.RollbackAsync(cancellationToken);
    }

    /// <summary>The <c>{ url, expiresAt }</c> body (09.7.2 plan §5).</summary>
    private sealed record VideoUrlBody(string Url, DateTimeOffset ExpiresAt);

    /// <summary>The ProblemDetails members these tests assert on (09 README D7).</summary>
    private sealed record ProblemBody(string Code, int Status);

    /// <summary>Keeps every rendered log line, so a test can scan it for a leaked URL.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        internal string Text
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _messages);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._gate)
                {
                    owner._messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
