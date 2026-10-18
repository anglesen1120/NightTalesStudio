using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Features.Stories;
using NightTales.Application.Rendering;
using NightTales.Domain.Assets;
using NightTales.Domain.Rendering;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Storage;
using NightTales.RenderNode.Contract;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Rendering;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>The fixed instant every ingest test's clock and seeded row are stamped from.</summary>
/// <remarks>
/// One instant for the whole class database keeps the retention stamp (<c>ingest time + 30 days</c>) and the
/// seeded timestamps comparable by a single SQL predicate: the tests never read the clock, they compare the
/// stored instant against <see cref="Instant"/>.
/// </remarks>
internal static class IngestTime
{
    /// <summary>The <c>IClock</c> reading of every test in this folder.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A <c>timestamptz</c> literal PostgreSQL parses back to the same instant.</summary>
    /// <param name="instant">The instant to render.</param>
    /// <returns>The round-tripable literal.</returns>
    public static string Literal(DateTimeOffset instant) => instant.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>One seeded owner, episode, story version, node and <c>render_jobs</c> row.</summary>
/// <param name="UserId">The owner of the row and of the episode.</param>
/// <param name="EpisodeId">The episode the render belongs to (seeded <c>rendering</c>).</param>
/// <param name="StoryVersionId">The approved story version the params name.</param>
/// <param name="RenderJobId">The <c>render_jobs</c> row the ingest is handed.</param>
/// <param name="NodeId">The <c>worker_nodes</c> row the task was submitted to.</param>
/// <param name="ReservationId">The attempt's reservation, the id the settle and the refund are keyed by.</param>
/// <param name="TaskId">The adapter's task id, as the row stores it.</param>
internal sealed record IngestWorld(
    Guid UserId,
    Guid EpisodeId,
    Guid StoryVersionId,
    Guid RenderJobId,
    Guid NodeId,
    Guid ReservationId,
    Guid TaskId)
{
    /// <summary>The BD7 key of this attempt's video output.</summary>
    public string VideoKey => $"media/{UserId:D}/{EpisodeId:D}/{RenderJobId:D}/video.mp4";

    /// <summary>The BD7 key of this attempt's subtitle output.</summary>
    public string SubtitlesKey => $"media/{UserId:D}/{EpisodeId:D}/{RenderJobId:D}/subtitles.srt";
}

/// <summary>The knobs of one seeded row: its state, its render attempt number and its params.</summary>
internal sealed record IngestSeed
{
    /// <summary>The <c>render_jobs.status</c> the poller left behind.</summary>
    public string Status { get; init; } = "ingesting";

    /// <summary>
    /// The episode's status the seed leaves behind: <c>rendering</c> for every normal ingest, and something else
    /// when the test needs the episode to have moved on before the ingest runs — the refused-attach branch, where
    /// the output is real but there is no episode left to attach it to (C2).
    /// </summary>
    public string EpisodeStatus { get; init; } = "rendering";

    /// <summary>The stored stage, as the adapter reported it.</summary>
    public string Stage { get; init; } = "done";

    /// <summary>The stored percent.</summary>
    public short Percent { get; init; } = 100;

    /// <summary>The attempt number of this <em>render</em> (never the scheduler's attempt count).</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>Whether the owner asked for a cancel before the ingest ran (WT51).</summary>
    public bool CancelRequested { get; init; }

    /// <summary>The <c>params.subtitles.enabled</c> the verdict is evaluated against.</summary>
    public bool SubtitlesEnabled { get; init; } = true;

    /// <summary>The <c>params.aspect</c> the resolution check is evaluated against.</summary>
    public string Aspect { get; init; } = "landscape";
}

/// <summary>Seeds and reads the ingest's world with raw SQL, the <c>PollRenderJobTests</c> way.</summary>
internal static class IngestWorlds
{
    /// <summary>
    /// One owner with a <c>rendering</c> episode, its story version, an active worker node and a
    /// <c>render_jobs</c> row in <paramref name="seed"/>'s state.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="cancellationToken">The test's cancellation.</param>
    /// <param name="seed">The row's state; the defaults are the poller's hand-over of a succeeded render.</param>
    /// <returns>The seeded ids.</returns>
    public static async Task<IngestWorld> SeedAsync(
        TestDatabase database,
        CancellationToken cancellationToken,
        IngestSeed? seed = null)
    {
        var world = seed ?? new IngestSeed();
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(database, actor).CreateAsync(ct: cancellationToken);
        var series = await new SeriesBuilder(database, actor).CreateAsync(user.Id, ct: cancellationToken);
        var episode = await new EpisodeBuilder(database, actor, new FakeClock(IngestTime.Start))
            .CreateAsync(user.Id, series.Id, ct: cancellationToken);

        var draft = IngestBodies.Draft();
        var version = StoryVersion.Create(
            user.Id,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            IngestTime.Start);

        await using (var seedWriter = database.CreateAppDbContext(actor, new FakeClock(IngestTime.Start)))
        {
            seedWriter.StoryVersions.Add(version);
            await seedWriter.SaveChangesAsync(cancellationToken);
        }

        var nodeId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var reservationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();

        await database.ExecuteAsync(
            $"UPDATE episodes SET status = '{world.EpisodeStatus}' WHERE id = '{episode.Id}'",
            cancellationToken);

        await database.ExecuteAsync(
            "INSERT INTO worker_nodes (id, name, base_url, capacity, state, last_heartbeat_at, gpu_model, "
            + "tls_cert_sha256, created_at, updated_at) "
            + $"VALUES ('{nodeId}', 'ingest-node-{nodeId:N}', 'https://127.0.0.1:8443', 2, 'active', now(), "
            + "'RTX 4090', '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF', now(), now())",
            cancellationToken);

        await database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, worker_node_id, adapter_task_id, submitted_at, started_at, next_poll_at, "
            + "cancel_requested_at) "
            + $"VALUES ('{jobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{reservationId}', '{world.Status}', "
            + $"'{world.Stage}', {world.Percent}, "
            + $"'{IngestBodies.ParamsJson(version.Id, world.SubtitlesEnabled, world.Aspect)}'::jsonb, "
            + $"{world.Attempt}, '{nodeId}', '{taskId}', '{IngestTime.Literal(IngestTime.Start.AddMinutes(-10))}', "
            + $"'{IngestTime.Literal(IngestTime.Start.AddMinutes(-5))}', '{IngestTime.Literal(IngestTime.Start)}', "
            + (world.CancelRequested ? $"'{IngestTime.Literal(IngestTime.Start)}'" : "NULL")
            + ")",
            cancellationToken);

        return new IngestWorld(user.Id, episode.Id, version.Id, jobId, nodeId, reservationId, taskId);
    }
}

/// <summary>The node bodies the ingest reads: a <c>render-status.v1</c> and the check it carries.</summary>
internal static class IngestBodies
{
    /// <summary>The SRT the fake download client serves for a kept subtitle track.</summary>
    /// <remarks>
    /// Low entropy on purpose (the gitleaks pre-commit rule) and greppable: a test asserts the ingest never
    /// logs it (AC11).
    /// </remarks>
    public const string DefaultSubtitles = "1\n00:00:00,000 --> 00:00:02,000\nsrt-canary-aaaa\n\n";

    /// <summary>A word of the seeded story text; the ingest must never log it (AC11).</summary>
    public const string StoryTextMarker = "riverside";

    /// <summary>The title of the seeded story; the ingest must never log it either (AC11).</summary>
    public const string StoryTitle = "The pier at dawn";

    /// <summary>A complete <c>render-params.v1</c> document (05 D11), the <c>PollRenderJobTests</c> shape.</summary>
    /// <param name="storyVersionId">The version the params name.</param>
    /// <param name="subtitlesEnabled">Whether the attempt requested subtitles.</param>
    /// <param name="aspect">The series' aspect wire name.</param>
    /// <returns>The serialised params.</returns>
    public static string ParamsJson(Guid storyVersionId, bool subtitlesEnabled = true, string aspect = "landscape") =>
        JsonSerializer.Serialize(new RenderParamsV1(
            RenderParamsV1.CurrentMapperVersion,
            storyVersionId,
            aspect,
            "vi",
            "kokoro:test",
            Guid.CreateVersion7(),
            "style=test@0123abcd;neg=v1",
            new ImagePlanSummary(3, 1, 2, 1, 0, [], ["paragraph:1"], "style=test@0123abcd;neg=v1"),
            new SubtitlesParams(subtitlesEnabled, $"{aspect}@1"),
            null,
            3));

    /// <summary>A <c>succeeded</c> status body carrying <paramref name="output"/> and <paramref name="check"/>.</summary>
    /// <param name="world">The seeded world the ids come from.</param>
    /// <param name="output">The declared output block.</param>
    /// <param name="check">The measured check, or <c>null</c> for a body without one.</param>
    /// <returns>The body.</returns>
    public static RenderStatusV1 Succeeded(IngestWorld world, RenderOutputV1 output, RenderCheckV1? check) =>
        Raw(world, output, check is null ? null : Element(check));

    /// <summary>A <c>succeeded</c> status body whose <c>check</c> block is raw JSON, not a parsed type.</summary>
    /// <param name="world">The seeded world the ids come from.</param>
    /// <param name="output">The declared output block.</param>
    /// <param name="check">The block as the adapter re-serves it (<see cref="EmptyCheck"/> is the malformed one).</param>
    /// <returns>The body.</returns>
    public static RenderStatusV1 SucceededRaw(IngestWorld world, RenderOutputV1 output, JsonElement? check) =>
        Raw(world, output, check);

    private static RenderStatusV1 Raw(IngestWorld world, RenderOutputV1 output, JsonElement? check) =>
        new(
            RenderNodeSchemas.RenderStatus,
            world.TaskId,
            world.RenderJobId,
            RenderStates.Succeeded,
            "done",
            100,
            false,
            IngestTime.Start,
            IngestTime.Start,
            IngestTime.Start.AddMinutes(5),
            null,
            output,
            check,
            null,
            null);

    /// <summary>The <c>output</c> block of a body the node reports as finished.</summary>
    /// <param name="video">What the node declares for the video.</param>
    /// <param name="subtitles">What it declares for the subtitle track, or <c>null</c> for none.</param>
    /// <param name="narrationSeconds">The narration length the <c>duration</c> check is compared against.</param>
    /// <returns>The block.</returns>
    public static RenderOutputV1 Output(ServedOutput video, ServedOutput? subtitles, double narrationSeconds = 301.37) =>
        new(
            new RenderFileV1(video.DeclaredBytes, video.DeclaredSha256, null),
            subtitles is null
                ? null
                : new RenderFileV1(subtitles.DeclaredBytes, subtitles.DeclaredSha256, subtitles.Cues),
            narrationSeconds);

    /// <summary>The by-reference block of a status: the node's own JSON, exactly as the adapter re-serves it.</summary>
    /// <param name="check">The measured check.</param>
    /// <returns>The element.</returns>
    public static JsonElement Element(RenderCheckV1 check) =>
        JsonSerializer.SerializeToElement(check, RenderNodeJson.Strict);

    /// <summary>What the node answers for a body without a check: fail-closed territory (P2).</summary>
    /// <returns>The element.</returns>
    public static JsonElement EmptyCheck() => JsonSerializer.SerializeToElement(new JsonObject(), RenderNodeJson.Strict);

    /// <summary>A check whose every measure passes the shipped policy.</summary>
    /// <param name="errors">The tool's error list; empty means the container probed cleanly.</param>
    /// <param name="warnings">The tool's warnings.</param>
    /// <param name="videoCodec">The video codec the <c>streams</c> check reads.</param>
    /// <param name="audioCodec">The audio codec the <c>streams</c> check reads.</param>
    /// <param name="width">The measured frame width.</param>
    /// <param name="height">The measured frame height.</param>
    /// <param name="durationS">The container duration the drift check reads.</param>
    /// <param name="narrationS">The narration duration the drift check reads.</param>
    /// <param name="integratedLufs">The integrated loudness the band check reads.</param>
    /// <param name="truePeakDbtp">The true peak the ceiling check reads.</param>
    /// <param name="longestSilenceS">The longest silence the limit check reads.</param>
    /// <param name="subtitles">The subtitle measures, or <c>null</c> for a report without the block.</param>
    /// <returns>The check.</returns>
    public static RenderCheckV1 Check(
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? warnings = null,
        string videoCodec = "h264",
        string audioCodec = "aac",
        int width = 1920,
        int height = 1080,
        double durationS = 302.1,
        double narrationS = 301.37,
        double integratedLufs = -15,
        double truePeakDbtp = -1.6,
        double longestSilenceS = 1.2,
        RenderCheckSubtitlesV1? subtitles = null)
    {
        var measures = subtitles ?? Subtitles();

        return new RenderCheckV1(
            RenderNodeSchemas.RenderCheck,
            "1.0.0",
            "7.1",
            new RenderCheckInputV1("final.mp4", 4096, HashOf("nts-check-input")),
            new RenderCheckOutputV1("final.norm.mp4", 4096, HashOf("nts-check-output"), true),
            new RenderCheckContainerV1("mp4", durationS, true),
            new RenderCheckVideoV1(videoCodec, "yuv420p", width, height, 24),
            new RenderCheckAudioV1(audioCodec, 48000, 2),
            new RenderCheckNarrationV1(narrationS),
            new RenderCheckLoudnessV1(
                new RenderCheckLoudnessMeasureV1(integratedLufs, truePeakDbtp, 5.1),
                new RenderCheckLoudnessMeasureV1(integratedLufs, truePeakDbtp, 5.1),
                "linear"),
            new RenderCheckSilenceV1(longestSilenceS, 3.4),
            measures,
            warnings ?? [],
            errors ?? []);
    }

    /// <summary>A subtitled render's subtitle measures, all inside the policy.</summary>
    /// <param name="cues">The cue count.</param>
    /// <param name="present">Whether the node found an SRT at all.</param>
    /// <param name="coverageShare">The covered share of the video.</param>
    /// <param name="textMatchesScript">Whether every cue matched the script.</param>
    /// <returns>The block.</returns>
    public static RenderCheckSubtitlesV1 Subtitles(
        int cues = 212,
        bool present = true,
        double coverageShare = 0.86,
        bool? textMatchesScript = true) =>
        new(present, cues, 0, 0, 0, 0, coverageShare, 42, 0, 12.5, 0, textMatchesScript, 0);

    /// <summary>The smallest clean story document the version factory accepts; its text is never rendered here.</summary>
    /// <returns>The document.</returns>
    public static StoryContentV1 Draft() => new(
        StoryTitle,
        new StorySegmentV1("Grey mist drifted over the riverside.", ["quiet riverside morning"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>The lowercase hex SHA-256 of a canary string, so no hash is ever a literal in this folder.</summary>
    /// <param name="text">The canary.</param>
    /// <returns>The 64-character hash.</returns>
    public static string HashOf(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>The order of the externally visible calls of one ingest, shared by the fakes that make them.</summary>
internal sealed class IngestOrder
{
    private readonly Lock _gate = new();
    private readonly List<string> _entries = [];

    /// <summary>Every entry, in the order it was appended.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Appends one entry.</summary>
    /// <param name="entry">The entry, e.g. <c>blob.put:media/…/video.mp4</c>.</param>
    public void Add(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }
}

/// <summary>How the fake download client answers one call.</summary>
internal enum DownloadBehaviour
{
    /// <summary>Streams the declared bytes and returns their honest hash.</summary>
    Bytes,

    /// <summary>Answers <c>adapter_output_absent</c> and writes nothing (05 D10).</summary>
    OutputAbsent,

    /// <summary>Writes the scripted prefix and throws (a destination that refused a write mid-stream, H1).</summary>
    Throw,

    /// <summary>Answers a scripted mapped failure and writes nothing (a transient, deferred or not-ready node).</summary>
    Failure,
}

/// <summary>One scripted answer of <see cref="FakeRenderDownloadClient"/>.</summary>
/// <remarks>
/// The two sides of an ingest are kept apart on purpose: <see cref="DeclaredBytes"/>/<see cref="DeclaredSha256"/>
/// are what the node's own status body claims, while <see cref="Literal"/> (or the generated sequence) is what the
/// client streams. A test that tampers with the served file therefore leaves the declaration alone, which is
/// exactly the sha256 mismatch AC5 asks for.
/// </remarks>
internal sealed class ServedOutput
{
    /// <summary>The byte count the node's body declares.</summary>
    public required long DeclaredBytes { get; init; }

    /// <summary>The SHA-256 the node's body declares.</summary>
    public required string DeclaredSha256 { get; init; }

    /// <summary>The content type the client serves the output as.</summary>
    public required string ContentType { get; init; }

    /// <summary>What the client does with the request.</summary>
    public DownloadBehaviour Behaviour { get; init; } = DownloadBehaviour.Bytes;

    /// <summary>The bytes the client streams, or <c>null</c> for a generated sequence of the declared size.</summary>
    public byte[]? Literal { get; init; }

    /// <summary>Whether the served bytes differ from the declaration by one byte (AC5's tampering).</summary>
    public bool TamperFirstByte { get; init; }

    /// <summary>The cue count a subtitle declaration carries; <c>null</c> for a video block.</summary>
    public int? Cues { get; init; }

    /// <summary>How many bytes a <see cref="DownloadBehaviour.Throw"/> answer writes before it throws.</summary>
    public int ThrowAfterBytes { get; init; } = FakeRenderDownloadClient.ChunkBytes;

    /// <summary>The exception a <see cref="DownloadBehaviour.Throw"/> answer throws.</summary>
    public Exception? Throws { get; init; }

    /// <summary>The mapped failure a <see cref="DownloadBehaviour.Failure"/> answer returns.</summary>
    public RenderClientFailure? Failure { get; init; }
}

/// <summary>One recorded download call and what it did.</summary>
internal sealed class DownloadCall(RenderOutputKind kind, string taskId, long maxBytes, string? destinationPath)
{
    /// <summary>Which route was read.</summary>
    public RenderOutputKind Kind { get; } = kind;

    /// <summary>The adapter's task id the call addressed.</summary>
    public string TaskId { get; } = taskId;

    /// <summary>The cap the job handed the client (<c>Render:MaxVideoBytes</c> or <c>Render:MaxSubtitlesBytes</c>).</summary>
    public long MaxBytes { get; } = maxBytes;

    /// <summary>The file the destination stream was reading/writing, when it was a file at all.</summary>
    public string? DestinationPath { get; } = destinationPath;

    /// <summary>How many bytes the answer wrote.</summary>
    public long BytesWritten { get; set; }

    /// <summary>The hash the answer returned, when it returned one.</summary>
    public string? Sha256 { get; set; }

    /// <summary>The failure code of the answer, or <c>null</c> for success.</summary>
    public string? Outcome { get; set; }
}

/// <summary>
/// The 09.6.2-T1 download port with scripted answers: it streams configurable bytes (generated or literal),
/// honours the caller's cap, records every call and never allocates the file it serves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming is the point.</b> One 64 KiB buffer is the most this client ever holds, and the copy goes
/// straight into the caller's stream — the destination the ingest hands over is its own temp <c>FileStream</c>.
/// <see cref="LargestWriteBytes"/> and <see cref="BufferBytes"/> make that measurable, so the cap test can assert
/// the ingest aborted a 2 MiB output at its bound instead of buffering it.
/// </para>
/// <para>
/// <b>The cap.</b> A call whose declared total exceeds <c>maxBytes</c> writes one chunk past the bound and then
/// answers <c>render_output_invalid</c>: the model of a streaming read that was already in flight when the cap
/// tripped, which leaves partial bytes in the destination (H1).
/// </para>
/// </remarks>
internal sealed class FakeRenderDownloadClient : IRenderDownloadClient
{
    /// <summary>The size of the one buffer this client copies through.</summary>
    public const int ChunkBytes = 64 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<RenderOutputKind, Queue<ServedOutput>> _scripted = [];
    private readonly Dictionary<RenderOutputKind, int> _counts = [];
    private readonly List<DownloadCall> _calls = [];

    /// <summary>Runs before the first byte of every call: the hook the <c>MarkIngesting</c> ordering test reads.</summary>
    public Func<RenderOutputKind, ValueTask>? OnBeforeWrite { get; set; }

    /// <summary>Every call, in order.</summary>
    public IReadOnlyList<DownloadCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The largest buffer this client ever allocated.</summary>
    public static long BufferBytes => ChunkBytes;

    /// <summary>The largest single write it ever made.</summary>
    public long LargestWriteBytes { get; private set; }

    /// <summary>Every byte it ever served, across all calls.</summary>
    public long TotalWritten { get; private set; }

    /// <summary>How many video downloads ran.</summary>
    public int VideoCalls => Count(RenderOutputKind.Video);

    /// <summary>How many subtitle downloads ran.</summary>
    public int SubtitleCalls => Count(RenderOutputKind.Subtitles);

    /// <summary>Scripts one video answer: a generated sequence of <paramref name="bytes"/> bytes.</summary>
    /// <param name="bytes">The declared and served size.</param>
    /// <param name="contentType">The content type the client serves it as.</param>
    /// <param name="tamper">Whether one served byte differs from the declaration.</param>
    /// <returns>The scripted answer, for the body builder.</returns>
    public ServedOutput ServeVideo(long bytes, string contentType = IngestRenderOutputJob.VideoContentType, bool tamper = false)
    {
        var served = new ServedOutput
        {
            DeclaredBytes = bytes,
            DeclaredSha256 = GeneratedSha256(bytes),
            ContentType = contentType,
            TamperFirstByte = tamper,
        };

        return Enqueue(RenderOutputKind.Video, served);
    }

    /// <summary>Scripts one video answer streaming a literal body.</summary>
    /// <param name="content">The bytes to serve.</param>
    /// <param name="contentType">The content type the client serves it as.</param>
    /// <param name="tamper">Whether one served byte differs from the declaration.</param>
    /// <returns>The scripted answer, for the body builder.</returns>
    public ServedOutput ServeVideoBytes(
        byte[] content,
        string contentType = IngestRenderOutputJob.VideoContentType,
        bool tamper = false)
    {
        ArgumentNullException.ThrowIfNull(content);

        var served = new ServedOutput
        {
            DeclaredBytes = content.Length,
            DeclaredSha256 = HashOf(content),
            ContentType = contentType,
            Literal = content,
            TamperFirstByte = tamper,
        };

        return Enqueue(RenderOutputKind.Video, served);
    }

    /// <summary>Scripts a video answer that writes a prefix and then throws (a refused destination write).</summary>
    /// <param name="content">The bytes to write before throwing.</param>
    /// <param name="exception">The exception the destination raised.</param>
    /// <returns>The scripted answer.</returns>
    public ServedOutput ServeVideoThrow(byte[] content, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(exception);

        var served = new ServedOutput
        {
            DeclaredBytes = content.Length,
            DeclaredSha256 = HashOf(content),
            ContentType = IngestRenderOutputJob.VideoContentType,
            Behaviour = DownloadBehaviour.Throw,
            Literal = content,
            ThrowAfterBytes = content.Length,
            Throws = exception,
        };

        return Enqueue(RenderOutputKind.Video, served);
    }

    /// <summary>
    /// Scripts a video answer that fails without streaming a byte: the node answered transiently, deferred, or
    /// reported the output not ready (09.6.2-T1's mapped failures — the ingest's retried download kinds).
    /// </summary>
    /// <param name="failure">The mapped failure the client returns.</param>
    /// <param name="declaredBytes">The size the node's body still declares for the file.</param>
    /// <returns>The scripted answer, for the body builder.</returns>
    public ServedOutput ServeVideoFailure(RenderClientFailure failure, long declaredBytes = 4096)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var served = new ServedOutput
        {
            DeclaredBytes = declaredBytes,
            DeclaredSha256 = GeneratedSha256(declaredBytes),
            ContentType = IngestRenderOutputJob.VideoContentType,
            Behaviour = DownloadBehaviour.Failure,
            Failure = failure,
        };

        return Enqueue(RenderOutputKind.Video, served);
    }

    /// <summary>Scripts a subtitle answer streaming a literal SRT.</summary>
    /// <param name="text">The SRT body.</param>
    /// <param name="cues">The cue count the node declares for it.</param>
    /// <param name="contentType">The content type the client serves it as.</param>
    /// <returns>The scripted answer, for the body builder.</returns>
    public ServedOutput ServeSubtitles(
        string text = IngestBodies.DefaultSubtitles,
        int cues = 212,
        string contentType = IngestRenderOutputJob.SubtitlesContentType)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = Encoding.UTF8.GetBytes(text);
        var served = new ServedOutput
        {
            DeclaredBytes = bytes.Length,
            DeclaredSha256 = HashOf(bytes),
            ContentType = contentType,
            Literal = bytes,
            Cues = cues,
        };

        return Enqueue(RenderOutputKind.Subtitles, served);
    }

    /// <summary>
    /// Scripts a subtitle answer of <c>adapter_output_absent</c>: the node's body still declares a track, the
    /// download has none (05 D10 — the video is kept either way).
    /// </summary>
    /// <param name="declaredBytes">The size the body declares.</param>
    /// <param name="cues">The cue count the body declares.</param>
    /// <returns>The scripted answer, for the body builder.</returns>
    public ServedOutput ServeSubtitlesAbsent(long declaredBytes = 2048, int cues = 212)
    {
        var served = new ServedOutput
        {
            DeclaredBytes = declaredBytes,
            DeclaredSha256 = GeneratedSha256(declaredBytes),
            ContentType = IngestRenderOutputJob.SubtitlesContentType,
            Behaviour = DownloadBehaviour.OutputAbsent,
            Cues = cues,
        };

        return Enqueue(RenderOutputKind.Subtitles, served);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<DownloadResult>> DownloadAsync(
        WorkerNode node,
        string taskId,
        RenderOutputKind kind,
        Stream destination,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);

        ServedOutput served;
        DownloadCall call;
        lock (_gate)
        {
            served = Next(kind);
            call = new DownloadCall(kind, taskId, maxBytes, destination is FileStream file ? file.Name : null);
            _calls.Add(call);
        }

        if (OnBeforeWrite is { } hook)
        {
            await hook(kind);
        }

        if (served.Behaviour == DownloadBehaviour.Failure)
        {
            var failure = served.Failure
                ?? throw new InvalidOperationException("A Failure answer carries no RenderClientFailure.");

            call.Outcome = failure.OutcomeCode;

            return RenderClientErrors.ToError(failure);
        }

        if (served.Behaviour == DownloadBehaviour.OutputAbsent)
        {
            call.Outcome = "adapter_output_absent";

            return RenderClientErrors.ToError(new RenderClientFailure(
                RenderClientFailureKind.OutputAbsent,
                "adapter_output_absent",
                "scripted: the node has no such output",
                Alert: false,
                RetryAfter: null));
        }

        // A declared total above the cap is served up to one chunk past the bound and then refused (H1).
        var tripped = served.Behaviour == DownloadBehaviour.Bytes && served.DeclaredBytes > maxBytes;
        var limit = tripped ? maxBytes + ChunkBytes : served.DeclaredBytes;
        if (served.Behaviour == DownloadBehaviour.Throw)
        {
            limit = Math.Min(limit, served.ThrowAfterBytes);
        }

        var buffer = new byte[ChunkBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var written = 0L;

        try
        {
            while (written < limit)
            {
                var size = (int)Math.Min(ChunkBytes, limit - written);
                var chunk = buffer.AsSpan(0, size);

                if (served.Literal is { } literal)
                {
                    if (served.TamperFirstByte)
                    {
                        literal.CopyTo(chunk);
                        chunk[0] ^= 0xFF;
                    }
                    else
                    {
                        literal.AsSpan(0, size).CopyTo(chunk);
                    }
                }
                else
                {
                    Fill(chunk, written);
                    if (served.TamperFirstByte && written == 0)
                    {
                        chunk[0] ^= 0xFF;
                    }
                }

                await destination.WriteAsync(buffer.AsMemory(0, size), ct);
                hash.AppendData(buffer, 0, size);
                written += size;

                lock (_gate)
                {
                    LargestWriteBytes = Math.Max(LargestWriteBytes, size);
                }
            }

            if (served.Behaviour == DownloadBehaviour.Throw && served.Throws is { } exception)
            {
                // The destination refused a write after this many bytes: the client's own failure surfaces.
                throw exception;
            }
        }
        finally
        {
            lock (_gate)
            {
                TotalWritten += written;
                call.BytesWritten = written;
            }
        }

        if (tripped)
        {
            call.Outcome = "render_output_invalid";

            return RenderClientErrors.ToError(new RenderClientFailure(
                RenderClientFailureKind.TerminalFailure,
                RenderOutcomeCodes.RenderOutputInvalid,
                "scripted: the output exceeds the cap",
                Alert: false,
                RetryAfter: null));
        }

        var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        call.Sha256 = sha256;

        return new DownloadResult(written, sha256, served.ContentType);
    }

    /// <summary>The honest hash of the generated sequence of <paramref name="bytes"/> bytes.</summary>
    /// <param name="bytes">The sequence size.</param>
    /// <returns>The lowercase hex hash.</returns>
    private static string GeneratedSha256(long bytes)
    {
        var buffer = new byte[ChunkBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var written = 0L;

        while (written < bytes)
        {
            var size = (int)Math.Min(ChunkBytes, bytes - written);
            Fill(buffer.AsSpan(0, size), written);
            hash.AppendData(buffer, 0, size);
            written += size;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Byte <c>i</c> is a function of its offset alone, so a sequence is reproducible without storage.</summary>
    /// <param name="buffer">The slice to fill.</param>
    /// <param name="offset">The offset of the slice's first byte inside the whole sequence.</param>
    private static void Fill(Span<byte> buffer, long offset)
    {
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = (byte)(((offset + index) * 31 + 7) % 251);
        }
    }

    private static string HashOf(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private ServedOutput Enqueue(RenderOutputKind kind, ServedOutput served)
    {
        lock (_gate)
        {
            if (!_scripted.TryGetValue(kind, out var queue))
            {
                queue = new Queue<ServedOutput>();
                _scripted[kind] = queue;
            }

            queue.Enqueue(served);
        }

        return served;
    }

    private ServedOutput Next(RenderOutputKind kind)
    {
        if (!_scripted.TryGetValue(kind, out var queue) || queue.Count == 0)
        {
            throw new InvalidOperationException(
                $"The ingest downloaded '{kind}' but this test scripted no answer for it.");
        }

        _counts[kind] = _counts.GetValueOrDefault(kind) + 1;

        return queue.Dequeue();
    }

    private int Count(RenderOutputKind kind)
    {
        lock (_gate)
        {
            return _counts.GetValueOrDefault(kind);
        }
    }
}

/// <summary>One object the recording store accepted.</summary>
/// <param name="Bucket">The bucket the caller resolved.</param>
/// <param name="Key">The object key.</param>
/// <param name="ContentType">The content type it was stored with.</param>
/// <param name="Bytes">How many bytes the store consumed.</param>
/// <param name="Sha256">The lowercase hex hash of exactly those bytes.</param>
/// <param name="Content">The bytes themselves when they are small enough to compare.</param>
internal sealed record BlobPut(string Bucket, string Key, string ContentType, long Bytes, string Sha256, byte[]? Content);

/// <summary>
/// The 09.7.2-T1 blob port as a recorder: every PUT's key and bytes, every DELETE's key, one switch that makes
/// either fail, and one ordering log shared with the adapter client.
/// </summary>
internal sealed class RecordingBlobStore(IngestOrder order) : IBlobStore
{
    /// <summary>The largest object this store keeps for a content comparison.</summary>
    private const int MaxCapturedBytes = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Bucket, string Key), BlobPut> _objects = [];

    /// <summary>Every accepted (or refused) PUT, in order.</summary>
    public List<BlobPut> Puts { get; } = [];

    /// <summary>Every DELETE the job issued, in order.</summary>
    public List<(string Bucket, string Key)> Deletes { get; } = [];

    /// <summary>When set, every PUT answers this error instead of storing (the store is unavailable).</summary>
    public Error? PutFailure { get; set; }

    /// <summary>When set, a PUT of exactly this key answers this error instead of storing (one object refused).</summary>
    public Dictionary<string, Error> PutFailures { get; } = [];

    /// <summary>
    /// When set, every PUT pauses on it before answering. Two deliveries of one job that were both scripted to
    /// meet here are then both inside the final-transaction section of AC8's race.
    /// </summary>
    public Barrier? PutBarrier { get; set; }

    /// <summary>When set, every DELETE answers this error.</summary>
    public Error? DeleteFailure { get; set; }

    /// <summary>The keys currently stored.</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_gate)
            {
                return [.. _objects.Keys.Select(entry => entry.Key)];
            }
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<BlobInfo>> PutAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        order.Add($"blob.put:{key}");

        if (PutBarrier is { } barrier)
        {
            barrier.SignalAndWait(cancellationToken);
        }

        if (PutFailures.TryGetValue(key, out var refused))
        {
            return refused;
        }

        if (PutFailure is { } failure)
        {
            return failure;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        var bytes = buffer.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var put = new BlobPut(
            bucket,
            key,
            contentType,
            bytes.Length,
            sha256,
            bytes.Length <= MaxCapturedBytes ? bytes : null);

        lock (_gate)
        {
            _objects[(bucket, key)] = put;
            Puts.Add(put);
        }

        return new BlobInfo(bytes.Length, sha256);
    }

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken) =>
        OpenReadAsync(bucket, key, 0, cancellationToken);

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(
        string bucket,
        string key,
        long fromByte,
        CancellationToken cancellationToken)
    {
        if (!_objects.TryGetValue((bucket, key), out var put) || put.Content is not { } content)
        {
            return Task.FromResult<ErrorOr<BlobObject>>(Error.NotFound("asset_not_found", "scripted: no such key."));
        }

        var slice = content.AsSpan((int)Math.Min(fromByte, content.Length)).ToArray();

        return Task.FromResult<ErrorOr<BlobObject>>(
            new BlobObject(new MemoryStream(slice), put.ContentType, content.Length));
    }

    /// <inheritdoc />
    public Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        order.Add($"blob.delete:{key}");

        lock (_gate)
        {
            Deletes.Add((bucket, key));

            if (DeleteFailure is { } failure)
            {
                return Task.FromResult<ErrorOr<Success>>(failure);
            }

            _objects.Remove((bucket, key));
        }

        return Task.FromResult<ErrorOr<Success>>(Result.Success);
    }

    /// <inheritdoc />
    public Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<ErrorOr<bool>>(_objects.ContainsKey((bucket, key)));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListKeysAsync(
        string bucket,
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var key in Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            yield return key;
        }
    }

    /// <inheritdoc />
    public ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName) =>
        new Uri($"https://storage.test/{bucket}/{key}");
}

/// <summary>
/// The render client the ingest sees: the shared <see cref="FakeRenderClient"/> plus the recording the ingest
/// tests need — the DELETE count, an observation hook that runs <em>inside</em> the call, and an entry in the
/// shared order log so the cleanup can be placed after the commit.
/// </summary>
/// <param name="inner">The scripted client every call delegates to.</param>
/// <param name="order">The shared ordering log.</param>
internal sealed class OrderingRenderClient(FakeRenderClient inner, IngestOrder order) : IRenderClient
{
    /// <summary>Runs after the DELETE was recorded; the hook the after-commit test reads the database in.</summary>
    public Func<ValueTask>? OnDelete { get; set; }

    /// <summary>How many DELETEs ran.</summary>
    public int DeleteCalls { get; private set; }

    /// <inheritdoc />
    public Task<ErrorOr<AdapterAcceptedV1>> SubmitAsync(
        WorkerNode node,
        AdapterSubmitRequestV1 body,
        CancellationToken ct) =>
        inner.SubmitAsync(node, body, ct);

    /// <inheritdoc />
    public Task<ErrorOr<RenderStatusV1>> GetStatusAsync(WorkerNode node, string taskId, CancellationToken ct) =>
        inner.GetStatusAsync(node, taskId, ct);

    /// <inheritdoc />
    public Task<ErrorOr<Success>> CancelAsync(WorkerNode node, string taskId, CancellationToken ct) =>
        inner.CancelAsync(node, taskId, ct);

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> DeleteAsync(WorkerNode node, string taskId, CancellationToken ct)
    {
        DeleteCalls++;
        order.Add($"adapter.delete:{taskId}");

        if (OnDelete is { } hook)
        {
            await hook();
        }

        return await inner.DeleteAsync(node, taskId, ct);
    }
}

/// <summary>
/// The 03.4.2-T1 seam as a recorder: the two success-path members the ingest calls, an <c>images</c> block to
/// merge, and the two hooks a test needs — one that observes the database <em>inside</em> the merge (proving the
/// transaction is open and uncommitted) and one that forces a failure after <c>Evaluate</c> ran.
/// </summary>
internal sealed class IngestTerminalHooks : IRenderTerminalHooks
{
    /// <summary>Every member that was called, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The block <see cref="MergeImagesAsync"/> answers, or <c>null</c> for none.</summary>
    public JsonNode? Images { get; set; }

    /// <summary>Runs inside <see cref="MergeImagesAsync"/>, before the verdict is stored.</summary>
    public Func<ValueTask>? OnMerge { get; set; }

    /// <summary>Runs inside <see cref="RecordKeptVideoAsync"/>, after the row was marked <c>succeeded</c>.</summary>
    public Func<ValueTask>? OnKeptVideo { get; set; }

    /// <summary>The exception <see cref="RecordKeptVideoAsync"/> throws after the report and the assets are staged.</summary>
    public Exception? KeptVideoFailure { get; set; }

    /// <inheritdoc />
    public ValueTask RecordImageUsageAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
    {
        Calls.Add("usage");

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<RenderFailureClassification> ClassifyAsync(
        RenderJob job,
        RenderStatusV1 status,
        CancellationToken ct)
    {
        Calls.Add("classify");

        return ValueTask.FromResult(new RenderFailureClassification(RenderOutcomeCodes.RenderNodeError, null));
    }

    /// <inheritdoc />
    public async ValueTask<JsonNode?> MergeImagesAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
    {
        Calls.Add("images");

        await (OnMerge?.Invoke() ?? ValueTask.CompletedTask);

        return Images;
    }

    /// <inheritdoc />
    public async ValueTask RecordKeptVideoAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
    {
        Calls.Add("kept");

        if (KeptVideoFailure is { } failure)
        {
            throw failure;
        }

        await (OnKeptVideo?.Invoke() ?? ValueTask.CompletedTask);
    }
}

/// <summary>
/// The four money ports of one ingest in one recorder: the settle and the refund are keyed by reservation id,
/// which is what the success and failure paths are asserted on (09.6.3-T1's recorder shape).
/// </summary>
internal sealed class IngestMoney : IPriceCatalog, IImageCostEstimator, IBudgetGuard, ICreditLedger
{
    /// <summary>Every settlement, in order; the reservation id is the row's, never the job's.</summary>
    public List<ReservationId> Settles { get; } = [];

    /// <summary>Every refund, in order.</summary>
    public List<(Guid ReservationId, string Reason)> Refunds { get; } = [];

    /// <summary>The verdict the ledger answers for a settlement.</summary>
    public ErrorOr<Success> Settle { get; set; } = Result.Success;

    /// <summary>The exception the ledger throws instead of answering a settlement.</summary>
    public Exception? SettleThrows { get; set; }

    /// <summary>The exception the ledger throws instead of answering a refund.</summary>
    public Exception? RefundThrows { get; set; }

    /// <inheritdoc />
    public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
        Task.FromResult<ErrorOr<PriceVersion>>(
            PriceVersion.Create(1, IngestTime.Start.AddDays(-1), "{}", "{}", "hash", IngestTime.Start.AddDays(-1)));

    /// <inheritdoc />
    public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<CostBound> WorstCaseAsync(Aspect aspect, int maxTerms, int priceVersion, CancellationToken ct) =>
        Task.FromResult(new CostBound(1.23m));

    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null) =>
        ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

    /// <inheritdoc />
    public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
        Guid userId,
        CreditOperation op,
        Guid referenceId,
        UsageBound bound,
        CancellationToken ct) =>
        ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Guid.CreateVersion7()));

    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct)
    {
        Settles.Add(reservationId);

        if (SettleThrows is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(Settle);
    }

    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
    {
        Refunds.Add((reservationId.Value, reason));

        if (RefundThrows is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }
}

/// <summary>One line as the sink saw it.</summary>
/// <param name="EventId">The id the <c>LoggerMessage</c> declared.</param>
/// <param name="Level">The level the line was written at.</param>
/// <param name="Message">The rendered message.</param>
internal sealed record IngestLogEntry(int EventId, LogLevel Level, string Message);

/// <summary>
/// A log provider that keeps every line of every category: the only way to assert what the ingest never writes
/// (AC11 — no SRT body, no story text, no signed storage URL).
/// </summary>
internal sealed class IngestLogSink : ILoggerProvider
{
    /// <summary>Every line, in order.</summary>
    public List<IngestLogEntry> Entries { get; } = [];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Sink(this);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class Sink(IngestLogSink owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries)
            {
                owner.Entries.Add(new IngestLogEntry(eventId.Id, logLevel, formatter(state, exception)));
            }
        }
    }
}

/// <summary>Answers the ingest's statements against the class database, the <c>SqlProbe</c> way.</summary>
internal static class IngestDb
{
    /// <summary>The row's state, its code and whether its stored verdict passed, in one string.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns><c>status|error_code|verdict</c>.</returns>
    public static string Row(TestDatabase database, IngestWorld world) =>
        IngestSql(
            database,
            "SELECT status || '|' || coalesce(error_code, '-') || '|' || coalesce(quality_report->>'pass', '-')",
            world);

    /// <summary>The episode's status and its final video asset id.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns><c>status|final_video_asset_id</c>.</returns>
    public static string Episode(TestDatabase database, IngestWorld world) =>
        SqlProbe.ScalarText(
            database,
            "SELECT status || '|' || coalesce(final_video_asset_id::text, '-') FROM episodes "
            + $"WHERE id = '{world.EpisodeId}'");

    /// <summary>Every asset row of the episode: kind, key, type, size, hash, duration and the retention verdict.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <param name="retentionUntil">The instant <c>retention_until</c> must equal.</param>
    /// <returns>One string per row, ordered by kind.</returns>
    public static List<string> Assets(TestDatabase database, IngestWorld world, DateTimeOffset retentionUntil) =>
        SqlProbe.QueryStrings(
            database,
            "SELECT kind || '|' || storage_key || '|' || content_type || '|' || size_bytes::text || '|' || sha256 "
            + "|| '|' || coalesce(duration_sec::text, '-') || '|' || "
            + $"(retention_until = '{IngestTime.Literal(retentionUntil)}'::timestamptz)::text "
            + $"FROM assets WHERE episode_id = '{world.EpisodeId}' ORDER BY kind");

    /// <summary>How many asset rows the episode has.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns>The count.</returns>
    public static long AssetCount(TestDatabase database, IngestWorld world) =>
        SqlProbe.Count(database, $"SELECT count(*) FROM assets WHERE episode_id = '{world.EpisodeId}'");

    /// <summary>The number of <c>render_jobs</c> rows of the episode (a retry creates a second one).</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns>The count.</returns>
    public static long JobCount(TestDatabase database, IngestWorld world) =>
        SqlProbe.Count(database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'");

    /// <summary>
    /// The instants the success path stamps, each as the boolean "equals <paramref name="at"/>", in one string:
    /// the row's <c>finished_at</c>, the video asset's <c>created_at</c>, its <c>retention_until</c>, the audit
    /// row's <c>created_at</c> and the success event's <c>occurred_at</c>. All five read true when the ingest
    /// stamped them from the clock reading taken inside the final transaction, not from the one it entered the
    /// attempt with (C4).
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <param name="at">The instant the ingest leg finished at.</param>
    /// <param name="retentionUntil">The instant the video asset's retention must equal.</param>
    /// <returns>Five <c>true</c>/<c>false</c> flags, joined by <c>|</c>.</returns>
    public static string Stamps(
        TestDatabase database,
        IngestWorld world,
        DateTimeOffset at,
        DateTimeOffset retentionUntil) =>
        SqlProbe.ScalarText(
            database,
            "SELECT (SELECT (finished_at = "
            + $"'{IngestTime.Literal(at)}'::timestamptz)::text FROM render_jobs WHERE id = '{world.RenderJobId}')"
            + " || '|' || (SELECT (created_at = "
            + $"'{IngestTime.Literal(at)}'::timestamptz)::text || '|' || (retention_until = "
            + $"'{IngestTime.Literal(retentionUntil)}'::timestamptz)::text FROM assets "
            + $"WHERE episode_id = '{world.EpisodeId}' AND kind = 'video')"
            + " || '|' || (SELECT (created_at = "
            + $"'{IngestTime.Literal(at)}'::timestamptz)::text FROM audit_logs WHERE entity_id = '{world.RenderJobId}')"
            + " || '|' || (SELECT (occurred_at = "
            + $"'{IngestTime.Literal(at)}'::timestamptz)::text FROM outbox_messages "
            + $"WHERE type = 'render_succeeded.v1' AND user_id = '{world.UserId}')");

    /// <summary>How many outbox rows of one type the owner has.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <param name="type">The event type, e.g. <c>render_succeeded.v1</c>.</param>
    /// <returns>The count.</returns>
    public static long Outbox(TestDatabase database, IngestWorld world, string type) =>
        SqlProbe.Count(
            database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{type}' AND user_id = '{world.UserId}'");

    /// <summary>
    /// The success event's payload as <c>renderJobId|episodeId|has-sha256|has-storageKey|member-names</c>: the
    /// names, sorted, are what makes "ids only" checkable (AC1 — no blob key, no URL, no content).
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns>The rendered payload verdict.</returns>
    public static string SucceededPayload(TestDatabase database, IngestWorld world) =>
        SqlProbe.ScalarText(
            database,
            "SELECT (payload->>'renderJobId') || '|' || (payload->>'episodeId') || '|' || (payload ? 'sha256')::text "
            + "|| '|' || (payload ? 'storageKey')::text || '|' "
            + "|| (SELECT string_agg(k, ',' ORDER BY k) FROM jsonb_object_keys(payload) AS k(key)) "
            + $"FROM outbox_messages WHERE type = 'render_succeeded.v1' AND user_id = '{world.UserId}'");

    /// <summary>The failure event's payload as <c>renderJobId|code|final</c>.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns>The rendered payload verdict.</returns>
    public static string FailedPayload(TestDatabase database, IngestWorld world) =>
        SqlProbe.ScalarText(
            database,
            "SELECT (payload->>'renderJobId') || '|' || (payload->>'errorCode') || '|' || (payload->>'final') "
            + $"FROM outbox_messages WHERE type = 'render_failed.v1' AND user_id = '{world.UserId}'");

    /// <summary>
    /// Every <c>render_jobs</c> row of the episode as <c>id|attempt|status</c>, ordered by attempt — the probe
    /// the retry verdict is read with (a transient code on attempt 1 queues an <c>attempt = 2</c> row).
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns>One string per row of the episode.</returns>
    public static List<string> JobRows(TestDatabase database, IngestWorld world) =>
        SqlProbe.QueryStrings(
            database,
            "SELECT id::text || '|' || attempt::text || '|' || status FROM render_jobs "
            + $"WHERE episode_id = '{world.EpisodeId}' ORDER BY attempt");

    /// <summary>Every audit row of the attempt: action, actor, scopes, entity and the video hash it names.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns>One string per row.</returns>
    public static List<string> Audit(TestDatabase database, IngestWorld world) =>
        SqlProbe.QueryStrings(
            database,
            "SELECT action || '|' || actor_type || '|' || category || '|' || visibility || '|' || entity_type "
            + "|| '|' || coalesce(data->>'videoSha256', '-') || '|' || (data ? 'content')::text "
            + $"FROM audit_logs WHERE entity_id = '{world.RenderJobId}'");

    /// <summary>How many audit rows the attempt has.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns>The count.</returns>
    public static long AuditCount(TestDatabase database, IngestWorld world) =>
        SqlProbe.Count(database, $"SELECT count(*) FROM audit_logs WHERE entity_id = '{world.RenderJobId}'");

    /// <summary>One expression of the row's quality report, as text.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <param name="expression">The <c>quality_report</c> expression, e.g. <c>->'images'->>'schema'</c>.</param>
    /// <returns>The value, or the empty string when the column is null.</returns>
    public static string Report(TestDatabase database, IngestWorld world, string expression) =>
        SqlProbe.ScalarText(
            database,
            $"SELECT coalesce(quality_report {expression}, '-') FROM render_jobs WHERE id = '{world.RenderJobId}'");

    /// <summary>How many checks the stored verdict carries.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns>The count.</returns>
    public static long ReportChecks(TestDatabase database, IngestWorld world) =>
        SqlProbe.Count(
            database,
            "SELECT coalesce(jsonb_array_length(quality_report->'checks'), 0) FROM render_jobs "
            + $"WHERE id = '{world.RenderJobId}'");

    /// <summary>One check of the stored verdict, as <c>value|threshold|pass</c>.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The seeded world.</param>
    /// <param name="id">The check id, e.g. <c>subtitles_present</c>.</param>
    /// <returns>The rendered check.</returns>
    public static string Check(TestDatabase database, IngestWorld world, string id) =>
        SqlProbe.ScalarText(
            database,
            "SELECT coalesce(entry->>'value', '-') || '|' || coalesce(entry->>'threshold', '-') || '|' "
            + "|| (entry->>'pass') FROM render_jobs, jsonb_array_elements(quality_report->'checks') AS entry "
            + $"WHERE render_jobs.id = '{world.RenderJobId}' AND entry->>'id' = '{id}'");

    private static string IngestSql(TestDatabase database, string select, IngestWorld world) =>
        SqlProbe.ScalarText(database, $"{select} FROM render_jobs WHERE id = '{world.RenderJobId}'");
}

/// <summary>
/// The Worker host of one ingest test: every port the ingest talks to is one of the recorded fakes above, and
/// nothing else about the composition root is changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The graph is the Worker's.</b> <see cref="WorkerHost"/> composes the production service collection, so the
/// registrations this task owns (<c>AddRenderJobs</c>/<c>AddRenderIngest</c>, the attempt port, the
/// <c>Render:Ingest</c>/<c>Assets</c> binding and its <c>ValidateOnStart</c> validator) are exercised by every
/// test: a missing registration surfaces as a resolution failure, not as a silent skip. The temp directory is the
/// only value a test overrides — the validator creates it on start, and it is deleted with the host.
/// </para>
/// <para>
/// <b>The caps are small on purpose.</b> <c>Render:MaxVideoBytes</c> is 1 MiB, so a cap trip is a megabyte of
/// hashing instead of two gigabytes; the shipped 2 GiB default is not what a test needs to prove.
/// </para>
/// <para>
/// <b>Attempts.</b> The ingest reads the scheduler's attempt through <c>IJobAttempt</c>. A test drives it through
/// <c>JobAttemptScope.Set</c> (<see cref="RunAsync(Guid, int, int)"/>), which is exactly what the Worker's job
/// filter publishes; outside a scope the port answers "one attempt, and it is the last".
/// </para>
/// </remarks>
internal sealed class IngestHarness : IAsyncDisposable
{
    /// <summary>The <c>Render:MaxVideoBytes</c> this host binds.</summary>
    public const long VideoCapBytes = 1024 * 1024;

    /// <summary>The <c>Render:MaxSubtitleBytes</c> this host binds.</summary>
    public const long SubtitleCapBytes = 64 * 1024;

    /// <summary>The <c>Assets:UnapprovedRetentionDays</c> this host binds (WT56).</summary>
    public const int UnapprovedRetentionDays = RenderIngestOptions.DefaultUnapprovedRetentionDays;

    /// <summary>
    /// How many attempts the ingest is allowed: one, plus the <c>Retries</c> its <c>[JobPolicy]</c> declares
    /// (BD7: five). Read from the attribute so the tests never carry a second copy of the number the
    /// scheduler enforces.
    /// </summary>
    public static int MaxAttempts => Policy.Retries + 1;

    /// <summary>The job's own retry policy, as the Infrastructure job filter reads it.</summary>
    private static JobPolicyAttribute Policy =>
        typeof(IngestRenderOutputJob).GetCustomAttribute<JobPolicyAttribute>()
        ?? throw new InvalidOperationException("IngestRenderOutputJob declares no [JobPolicy].");

    private readonly CancellationToken _cancellationToken;

    private IngestHarness(string tempDirectory, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        TempDirectory = tempDirectory;
        Adapter = new OrderingRenderClient(Render, Order);
        Blobs = new RecordingBlobStore(Order);
    }

    /// <summary>The instant the host's clock answers, and every seeded row is stamped from.</summary>
    public static DateTimeOffset Start => IngestTime.Start;

    /// <summary>The media bucket the ingest writes to (<c>nt-media-&lt;env&gt;</c>, BD10).</summary>
    public static string MediaBucket => BlobLocator.BucketFor(AssetKind.Video, new StorageSettings());

    /// <summary>The retention stamp of an unapproved episode's asset.</summary>
    public static DateTimeOffset UnapprovedRetention => Start.AddDays(UnapprovedRetentionDays);

    /// <summary>This test's private spool directory, created by the options validator at start.</summary>
    public string TempDirectory { get; }

    /// <summary>The order of the blob and adapter calls of one attempt.</summary>
    public IngestOrder Order { get; } = new();

    /// <summary>The scripted node client.</summary>
    public FakeRenderClient Render { get; } = new();

    /// <summary>The render client the ingest resolves: <see cref="Render"/> plus the DELETE recording.</summary>
    public OrderingRenderClient Adapter { get; }

    /// <summary>The scripted, recorded download client.</summary>
    public FakeRenderDownloadClient Downloads { get; } = new();

    /// <summary>
    /// The host's clock. Every other test leaves it at <see cref="IngestTime.Start"/>; a test that needs the
    /// ingest leg to have taken real time advances it from a download hook and asserts the instants that were
    /// stamped from the clock reading taken inside the final transaction (C4).
    /// </summary>
    public FakeClock Clock { get; } = new(IngestTime.Start);

    /// <summary>The recorded blob store.</summary>
    public RecordingBlobStore Blobs { get; }

    /// <summary>The 03.4.2-T1 seam.</summary>
    public IngestTerminalHooks Hooks { get; } = new();

    /// <summary>The four money ports.</summary>
    public IngestMoney Money { get; } = new();

    /// <summary>Every line the host wrote.</summary>
    public IngestLogSink Logs { get; } = new();

    /// <summary>The started worker host.</summary>
    public WorkerHost Host { get; private set; } = null!;

    /// <summary>Starts a host whose ports are the fakes above and whose spool is a fresh temp directory.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="cancellationToken">The test's cancellation.</param>
    /// <returns>The started harness.</returns>
    public static async Task<IngestHarness> StartAsync(TestDatabase database, CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "nts-ingest-tests",
            Guid.NewGuid().ToString("N"));

        var harness = new IngestHarness(tempDirectory, cancellationToken);

        harness.Host = new WorkerHost(database, harness.Clock)
            .WithSetting(
                $"{RenderIngestOptions.SectionName}:{nameof(RenderIngestOptions.TempDirectory)}",
                tempDirectory)
            .WithSetting(
                $"{RenderIngestOptions.RetentionSectionName}:{nameof(RenderIngestOptions.UnapprovedRetentionDays)}",
                UnapprovedRetentionDays.ToString(CultureInfo.InvariantCulture))
            .WithSetting(
                $"{RenderOptions.SectionName}:{nameof(RenderOptions.MaxVideoBytes)}",
                VideoCapBytes.ToString(CultureInfo.InvariantCulture))
            .WithSetting(
                $"{RenderOptions.SectionName}:{nameof(RenderOptions.MaxSubtitleBytes)}",
                SubtitleCapBytes.ToString(CultureInfo.InvariantCulture))
            .ConfigureServices(services => harness.Replace(services));

        await harness.Host.StartAsync(cancellationToken);

        return harness;
    }

    /// <summary>The files left in the spool directory (empty is the required end state).</summary>
    /// <returns>The file names, or an empty list when the directory does not exist.</returns>
    public string[] TempFiles() =>
        Directory.Exists(TempDirectory) ? Directory.GetFiles(TempDirectory) : [];

    /// <summary>The lines of the ingest itself (its 6300-6339 and 6359 event-id block), without the host's own
    /// start-up noise.</summary>
    /// <returns>The ingest's lines, in order.</returns>
    public IReadOnlyList<IngestLogEntry> IngestLines() =>
        [.. Logs.Entries.Where(entry => entry.EventId is >= 6300 and <= 6339 or 6359)];

    /// <summary>Runs the ingest of <paramref name="renderJobId"/> the way the scheduler does: in its own scope.</summary>
    /// <param name="renderJobId">The <c>render_jobs</c> row.</param>
    /// <returns>A task that completes when the attempt ended.</returns>
    public Task RunAsync(Guid renderJobId) => RunAsync(renderJobId, _cancellationToken);

    /// <summary>
    /// Runs the ingest of <paramref name="renderJobId"/> under a token of the caller's own: the shutdown path a
    /// test drives by cancelling it mid-download, while the host keeps the token it started with.
    /// </summary>
    /// <param name="renderJobId">The <c>render_jobs</c> row.</param>
    /// <param name="cancellationToken">The token the attempt runs under.</param>
    /// <returns>A task that completes when the attempt ended.</returns>
    public async Task RunAsync(Guid renderJobId, CancellationToken cancellationToken)
    {
        using var scope = Host.CreateScope();

        await scope.ServiceProvider
            .GetRequiredService<IngestRenderOutputJob>()
            .RunAsync(renderJobId, cancellationToken);
    }

    /// <summary>Runs the ingest of the last of <paramref name="maxAttempts"/> scheduler attempts.</summary>
    /// <param name="renderJobId">The <c>render_jobs</c> row.</param>
    /// <returns>A task that completes when the attempt ended.</returns>
    public Task RunLastAttemptAsync(Guid renderJobId) => RunAsync(renderJobId, MaxAttempts, MaxAttempts);

    /// <summary>Runs the ingest of one scheduler attempt, publishing the attempt the filter would.</summary>
    /// <param name="renderJobId">The <c>render_jobs</c> row.</param>
    /// <param name="attempt">The 1-based attempt now running.</param>
    /// <param name="maxAttempts">How many attempts the job class allows.</param>
    /// <returns>A task that completes when the attempt ended.</returns>
    public async Task RunAsync(Guid renderJobId, int attempt, int maxAttempts)
    {
        using var scope = JobAttemptScope.Set(attempt, maxAttempts);

        await RunAsync(renderJobId);
    }

    /// <summary>Runs one attempt that must throw, and answers the exception it threw.</summary>
    /// <param name="renderJobId">The <c>render_jobs</c> row.</param>
    /// <param name="attempt">The 1-based attempt now running.</param>
    /// <param name="maxAttempts">How many attempts the job class allows.</param>
    /// <returns>The thrown exception.</returns>
    public async Task<Exception> ThrowsAsync(Guid renderJobId, int attempt, int maxAttempts)
    {
        try
        {
            await RunAsync(renderJobId, attempt, maxAttempts);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return failure;
        }

        throw new InvalidOperationException(
            $"The ingest of job '{renderJobId}' was expected to throw so the scheduler retries it.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();

        try
        {
            if (Directory.Exists(TempDirectory))
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // The spool is under the platform temp directory: a leftover file is the OS's business, not a failure.
        }
    }

    private void Replace(IServiceCollection services)
    {
        services.RemoveAll<IRenderClient>();
        services.AddSingleton<IRenderClient>(Adapter);
        services.RemoveAll<IRenderDownloadClient>();
        services.AddSingleton<IRenderDownloadClient>(Downloads);
        services.RemoveAll<IBlobStore>();
        services.AddSingleton<IBlobStore>(Blobs);
        services.RemoveAll<IRenderTerminalHooks>();
        services.AddSingleton<IRenderTerminalHooks>(Hooks);
        services.RemoveAll<ICreditLedger>();
        services.AddSingleton<ICreditLedger>(Money);
        services.RemoveAll<IBudgetGuard>();
        services.AddSingleton<IBudgetGuard>(Money);
        services.RemoveAll<IPriceCatalog>();
        services.AddSingleton<IPriceCatalog>(Money);
        services.RemoveAll<IImageCostEstimator>();
        services.AddSingleton<IImageCostEstimator>(Money);
        services.AddLogging(logging => logging.AddProvider(Logs));

        // The readiness prober is the Worker's own BackgroundService and writes its 0-byte marker object through
        // whatever IBlobStore the host resolves — which here is the recorder. Left in place, every "the ingest
        // wrote nothing" assertion would see the marker (`_health/storage-ready.txt`) instead of the ingest's own
        // work. 09 README D13 ships the removal for exactly this (`StorageHealthTests.WithoutProber`), and the
        // prober has no part in the ingest's graph.
        services.WithoutStorageHealthProbe();
    }
}
