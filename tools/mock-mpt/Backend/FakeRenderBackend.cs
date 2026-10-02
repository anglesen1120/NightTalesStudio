using System.Collections.Concurrent;
using System.Security.Cryptography;
using ErrorOr;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.MockMpt.Fixtures;
using NightTales.MockMpt.Health;
using NightTales.MockMpt.Images;
using NightTales.MockMpt.Media;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt.Backend;

/// <summary>
/// The mock's render backend (11 README D5, 11.1.2-T1 Design): it accepts a submit, records the task and lets
/// its ticker walk <c>queued → running → checking → succeeded</c> over <c>MOCK_RENDER_SECONDS</c>, then fills
/// the terminal blocks and either succeeds or replays a D14 fixture. No MPT, no GPU, no OpenAI, no egress.
/// </summary>
/// <remarks>
/// The ticker is the <see cref="BackgroundService"/> half of this class (registered as both
/// <see cref="IRenderBackend"/> and a hosted service). Tasks live in the core's in-memory store, so a mock
/// restart loses them exactly like a node losing MPT's state — the intended <c>render_lost</c> path (05 D4).
/// </remarks>
public sealed partial class FakeRenderBackend : BackgroundService, IRenderBackend
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    private readonly ITaskStore _store;
    private readonly IImagePlanSink _planSink;
    private readonly FixtureSet _fixtures;
    private readonly CannedMediaSet _media;
    private readonly MockHealthState _health;
    private readonly MockMptOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FakeRenderBackend> _logger;
    private readonly ConcurrentDictionary<Guid, SubmittedTask> _tasks = new();
    private readonly ConcurrentDictionary<Guid, byte> _frozen = new();

    public FakeRenderBackend(
        ITaskStore store,
        IImagePlanSink planSink,
        FixtureSet fixtures,
        CannedMediaSet media,
        MockHealthState health,
        MockMptOptions options,
        TimeProvider clock,
        ILogger<FakeRenderBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(planSink);
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _planSink = planSink;
        _fixtures = fixtures;
        _media = media;
        _health = health;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<BackendAccepted>> SubmitAsync(
        RenderTaskRecord record,
        MptSubmitFields mpt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(mpt);

        _tasks[record.TaskId] = new SubmittedTask(mpt.VideoAspect, mpt.VideoScript, mpt.SubtitleEnabled);

        if (ResolveFixture(mpt) is { } fixtureId)
        {
            // The fixture carries its own terminal shape; the task is frozen so the ticker never advances it.
            var stored = await _store.TryGetAsync(record.TaskId, cancellationToken).ConfigureAwait(false) ?? record;
            var replayed = fixtureId.Apply(stored);
            _frozen[record.TaskId] = 0;
            if (replayed.Output?.Subtitles is not null)
            {
                // The replayed body advertises `output.subtitles`, so the download must exist too: write the
                // generated SRT (the fixture keeps its own advertised facts, like `output.video` vs the canned file).
                await WriteSubtitlesAsync(record.TaskId, mpt.VideoScript, cancellationToken).ConfigureAwait(false);
            }

            await _store.UpdateAsync(replayed, cancellationToken).ConfigureAwait(false);
            _planSink.Release(record.TaskId);
            LogFixtureReplay(record.TaskId, fixtureId.Id);
        }
        else
        {
            // The queued row the core inserted carries no MPT observables yet; fill them so a poll taken right
            // after the 202 has the same `mpt` shape the queued fixture pins.
            var stored = await _store.TryGetAsync(record.TaskId, cancellationToken).ConfigureAwait(false) ?? record;
            await _store.UpdateAsync(
                stored with { MptLastSnapshot = ProgressClock.Snapshot(RenderStages.Queued, 0) },
                cancellationToken).ConfigureAwait(false);
        }

        await PublishActiveTasksAsync(cancellationToken).ConfigureAwait(false);
        return new BackendAccepted($"mock-{record.TaskId:N}", record.SubmittedAt);
    }

    /// <inheritdoc />
    public async Task CancelAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        _frozen[record.TaskId] = 0;
        var stored = await _store.TryGetAsync(record.TaskId, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.IsTerminal)
        {
            return;
        }

        await _store.UpdateAsync(
            stored with
            {
                State = RenderStates.Cancelled,
                Stage = RenderStages.Done,
                Percent = 100,
                CancelRequested = true,
                FinishedAt = _clock.GetUtcNow(),
                MptLastSnapshot = ProgressClock.Snapshot(RenderStages.Done, 100),
            },
            cancellationToken).ConfigureAwait(false);
        RemoveSubtitle(record.TaskId);
        await PublishActiveTasksAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Deleted>> DeleteAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var stored = await _store.TryGetAsync(record.TaskId, cancellationToken).ConfigureAwait(false) ?? record;
        if (!stored.IsTerminal)
        {
            // MPT still runs the task: the contract's `adapter_task_busy` (C11, F08).
            return Error.Conflict(AdapterErrorCodes.TaskBusy);
        }

        _tasks.TryRemove(record.TaskId, out _);
        _frozen.TryRemove(record.TaskId, out _);
        RemoveSubtitle(record.TaskId);
        return Result.Deleted;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<OutputFile>> OpenOutputAsync(
        Guid taskId,
        OutputKind kind,
        CancellationToken cancellationToken)
    {
        if (!_tasks.TryGetValue(taskId, out var submitted))
        {
            return Error.NotFound(AdapterErrorCodes.TaskUnknown);
        }

        if (kind == OutputKind.Video)
        {
            var video = _media.ForAspect(submitted.Aspect);
            return new OutputFile(
                Open(video.Path),
                video.Bytes,
                video.Sha256,
                RenderNodeHeaders.VideoContentType);
        }

        var stored = await _store.TryGetAsync(taskId, cancellationToken).ConfigureAwait(false);
        var subtitlePath = _media.SubtitlePath(taskId);
        if (stored?.Output?.Subtitles is null || !File.Exists(subtitlePath))
        {
            return Error.NotFound(AdapterErrorCodes.OutputAbsent);
        }

        // Measured from the served file, never taken from the stored block: a replayed D14 fixture advertises
        // its own `output.subtitles` facts, exactly like its `output.video` against the canned MP4, so the
        // download must stay self-consistent (`Content-Length`/`X-Content-SHA256` = what is streamed).
        var content = await File.ReadAllBytesAsync(subtitlePath, cancellationToken).ConfigureAwait(false);
        return new OutputFile(
            new MemoryStream(content, writable: false),
            content.Length,
            Convert.ToHexStringLower(SHA256.HashData(content)),
            RenderNodeHeaders.SubRipContentType);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A mock must survive one bad tick; the log names the failure type, never body content.
                LogTickFailed(exception.GetType().Name);
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var total = TimeSpan.FromSeconds(_options.RenderSeconds);

        foreach (var taskId in await _store.ListNonTerminalAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_frozen.ContainsKey(taskId))
            {
                continue;
            }

            var stored = await _store.TryGetAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.IsTerminal || !_tasks.TryGetValue(taskId, out var submitted))
            {
                continue;
            }

            if (now - stored.SubmittedAt < total)
            {
                var (state, stage, percent) = ProgressClock.At(now - stored.SubmittedAt, total);
                await _store.UpdateAsync(
                    stored with
                    {
                        State = state,
                        Stage = stage,
                        Percent = percent,
                        MptLastSnapshot = ProgressClock.Snapshot(stage, percent),
                    },
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(stored.Stage, RenderStages.Checking, StringComparison.Ordinal))
            {
                // The post-processing stage: still running, 99 — 100 belongs to the terminal state only.
                await _store.UpdateAsync(
                    stored with
                    {
                        State = RenderStates.Running,
                        Stage = RenderStages.Checking,
                        Percent = 99,
                        MptLastSnapshot = ProgressClock.Snapshot(RenderStages.Checking, 100),
                    },
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            await CompleteAsync(stored, submitted, cancellationToken).ConfigureAwait(false);
        }

        await PublishActiveTasksAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteAsync(RenderTaskRecord stored, SubmittedTask submitted, CancellationToken cancellationToken)
    {
        var video = _media.ForAspect(submitted.Aspect);
        SubtitleFile? subtitles = null;
        if (submitted.Subtitles)
        {
            // Cue timing follows the canned file actually served, not the reported `narration_seconds`
            // (MOCK_VIDEO_SECONDS): the SRT is spread over the canned duration (11.1.2-T1 Design).
            subtitles = await WriteSubtitlesAsync(stored.TaskId, submitted.Script, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            RemoveSubtitle(stored.TaskId);
        }

        var images = FakePostProcessor.BuildImages(_planSink, stored.TaskId);
        _frozen[stored.TaskId] = 0;

        await _store.UpdateAsync(
            stored with
            {
                State = RenderStates.Succeeded,
                Stage = RenderStages.Done,
                Percent = 100,
                Output = FakePostProcessor.BuildOutput(video, subtitles, _options.VideoSeconds),
                Check = FakePostProcessor.BuildCheck(_fixtures, video, subtitles),
                Images = images,
                FinishedAt = _clock.GetUtcNow(),
                MptLastSnapshot = ProgressClock.Snapshot(RenderStages.Done, 100),
            },
            cancellationToken).ConfigureAwait(false);
        _planSink.Release(stored.TaskId);
    }

    private StatusFixture? ResolveFixture(MptSubmitFields mpt)
    {
        if (FixtureKeyword.Find(mpt) is { } keyword && _fixtures.Status(keyword) is { } fromKeyword)
        {
            return fromKeyword;
        }

        return _fixtures.Status(_options.Fixture);
    }

    private async Task PublishActiveTasksAsync(CancellationToken cancellationToken)
    {
        _health.SetActiveTasks(await _store.CountNonTerminalAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Generates the SubRip file for <paramref name="script"/> over the canned media duration and writes it
    /// where <see cref="CannedMediaSet.SubtitlePath"/> serves it from; returns what was written.
    /// </summary>
    private async Task<SubtitleFile> WriteSubtitlesAsync(Guid taskId, string script, CancellationToken cancellationToken)
    {
        var subtitles = SubtitleScript.Build(script, CannedMediaSet.CannedSeconds);
        await File.WriteAllBytesAsync(_media.SubtitlePath(taskId), subtitles.Content, cancellationToken)
            .ConfigureAwait(false);
        return subtitles;
    }

    private void RemoveSubtitle(Guid taskId)
    {
        var path = _media.SubtitlePath(taskId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "mock-mpt.fixture: task={TaskId} fixture={Fixture}")]
    private partial void LogFixtureReplay(Guid taskId, string fixture);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "mock-mpt.tick.failed: error={ErrorType}")]
    private partial void LogTickFailed(string errorType);

    private sealed record SubmittedTask(string Aspect, string Script, bool Subtitles);
}
