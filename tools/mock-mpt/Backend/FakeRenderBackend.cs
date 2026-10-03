using System.Collections.Concurrent;
using System.Security.Cryptography;
using ErrorOr;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.MockMpt.Failures;
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
    private readonly ConcurrentDictionary<Guid, InjectionPlan> _plans = new();
    private readonly IImageReportSelector _imageSelector;
    private readonly FailureInjector _injector;

    public FakeRenderBackend(
        ITaskStore store,
        IImagePlanSink planSink,
        IImageReportSelector imageSelector,
        FailureInjector injector,
        FixtureSet fixtures,
        CannedMediaSet media,
        MockHealthState health,
        MockMptOptions options,
        TimeProvider clock,
        ILogger<FakeRenderBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(planSink);
        ArgumentNullException.ThrowIfNull(imageSelector);
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _planSink = planSink;
        _imageSelector = imageSelector;
        _injector = injector;
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

        var submitted = new SubmittedTask(mpt.VideoAspect, mpt.VideoScript, mpt.SubtitleEnabled);
        _tasks[record.TaskId] = submitted;

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

            // A terminal replay needs the script no longer: only the aspect and the SRT path serve reads
            // (review fix MOCK-PERF-01). A frozen non-terminal replay keeps it for its later completion.
            if (RenderStates.IsTerminal(replayed.State))
            {
                _tasks[record.TaskId] = submitted with { Script = null };
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

            // Failure injection (11.1.2-T2): the plan is resolved once, here, from the keyword / env / rate
            // layers; the ticker applies it. A lost task is removed right away, so the first poll after the
            // 202 is already the 404 `adapter_task_unknown` of a node that lost its state (05 D4).
            var plan = _injector.Resolve(record.TaskId, record.RenderJobId, mpt);
            var clipBound = (_options.VideoSeconds + Math.Max(1, mpt.VideoClipDuration) - 1) / Math.Max(1, mpt.VideoClipDuration);
            _imageSelector.UseReport(record.TaskId, plan.Image, clipBound);
            if (plan.Mode.Kind == FailKind.LostTask)
            {
                await LoseTaskAsync(record.TaskId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _plans[record.TaskId] = plan;
            }
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
        DropScript(record.TaskId);
        _plans.TryRemove(record.TaskId, out _);
        _planSink.Release(record.TaskId);
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
        _plans.TryRemove(record.TaskId, out _);
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

            if (_plans.TryGetValue(taskId, out var plan)
                && await TryInjectAsync(stored, plan, now - stored.SubmittedAt, total, cancellationToken).ConfigureAwait(false))
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
            subtitles = await WriteSubtitlesAsync(stored.TaskId, submitted.Script!, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            RemoveSubtitle(stored.TaskId);
        }

        // The script has served its purpose (the SRT is written): a terminal task keeps only the aspect and
        // the SRT path reads need, so a long dev session cannot grow the backend with past scripts
        // (review fix MOCK-PERF-01).
        DropScript(stored.TaskId);

        var images = FakePostProcessor.BuildImages(_planSink, stored.TaskId);
        _frozen[stored.TaskId] = 0;

        await _store.UpdateAsync(
            stored with
            {
                State = RenderStates.Succeeded,
                Stage = RenderStages.Done,
                Percent = 100,
                Output = FakePostProcessor.BuildOutput(video, subtitles, _options.VideoSeconds),
                Check = FakePostProcessor.BuildCheck(_fixtures, video, subtitles, QualityCheckFixture(stored.TaskId)),
                Images = images,
                FinishedAt = _clock.GetUtcNow(),
                MptLastSnapshot = ProgressClock.Snapshot(RenderStages.Done, 100),
            },
            cancellationToken).ConfigureAwait(false);
        _planSink.Release(stored.TaskId);
        _plans.TryRemove(stored.TaskId, out _);
    }

    /// <summary>The failing <c>render-check.v1</c> fixture of a <c>quality:&lt;check&gt;</c> plan, else null.</summary>
    private string? QualityCheckFixture(Guid taskId) =>
        _plans.TryGetValue(taskId, out var plan) && plan.Mode.Kind == FailKind.Quality
            ? _fixtures.QualityCheckFixture(plan.Mode.QualityCheck!)
            : null;

    /// <summary>
    /// Applies an injected failure once the progress clock reaches its stage (11.1.2-T2 Design). Returns true
    /// when the task was handled (failed or frozen) and the normal progress must not run for it.
    /// </summary>
    private async Task<bool> TryInjectAsync(
        RenderTaskRecord stored,
        InjectionPlan plan,
        TimeSpan elapsed,
        TimeSpan total,
        CancellationToken cancellationToken)
    {
        const int timeoutPercent = 95;
        const int failedMptState = -1;

        var reached = ProgressClock.At(elapsed, total).Percent;
        if (plan.Mode.Kind == FailKind.Timeout)
        {
            if (reached < timeoutPercent)
            {
                return false;
            }

            // `timeout`: progress stops at assembling 95 % and the task never becomes terminal; the Worker
            // raises `render_timeout` (05 D5).
            _frozen[stored.TaskId] = 0;
            await _store.UpdateAsync(
                stored with
                {
                    State = RenderStates.Running,
                    Stage = RenderStages.Assembling,
                    Percent = timeoutPercent,
                    MptLastSnapshot = ProgressClock.Snapshot(RenderStages.Assembling, timeoutPercent),
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (ShapeOf(plan) is not { } shape || (reached < shape.Percent && elapsed < total))
        {
            return false;
        }

        var images = shape.WithImages ? FakePostProcessor.BuildImages(_planSink, stored.TaskId) : null;
        _frozen[stored.TaskId] = 0;
        await _store.UpdateAsync(
            stored with
            {
                State = RenderStates.Failed,
                Stage = shape.Stage,
                Percent = shape.Percent,
                Outcome = new RenderOutcomeV1(shape.Code, shape.Detail, OutcomeMessages.For(shape.Code)),
                Images = images,
                FinishedAt = _clock.GetUtcNow(),
                MptLastSnapshot = new MptStateV1(failedMptState, shape.Percent, shape.FailedStage, []),
            },
            cancellationToken).ConfigureAwait(false);
        DropScript(stored.TaskId);
        RemoveSubtitle(stored.TaskId);
        _planSink.Release(stored.TaskId);
        _plans.TryRemove(stored.TaskId, out _);
        return true;
    }

    /// <summary>
    /// The terminal failure a plan produces, or null when it succeeds. A stage-bound mode wins over the image
    /// report's own failure (the earlier row of the Design's precedence); only <c>moderated_all</c> and
    /// <c>funds</c> force the failed <c>materials</c> state.
    /// </summary>
    private static FailureShape? ShapeOf(InjectionPlan plan)
    {
        switch (plan.Mode.Kind)
        {
            case FailKind.TtsFailed:
                return new FailureShape(RenderStages.Voice, 20, RenderOutcomeCodes.TtsFailed, "audio", "audio", false);
            case FailKind.AssemblyFailed:
                return new FailureShape(
                    RenderStages.Assembling, 75, RenderOutcomeCodes.RenderAssemblyFailed, "video", "video", true);
            case FailKind.NodeError:
                return new FailureShape(
                    RenderStages.Queued, 0, RenderOutcomeCodes.RenderNodeError, "preflight", "preflight", false);
        }

        return plan.Image.Kind switch
        {
            ImageReportKind.ModeratedAll => new FailureShape(
                RenderStages.Images, 40, RenderOutcomeCodes.ImageModerated, "materials", "materials", true),
            ImageReportKind.Funds => new FailureShape(
                RenderStages.Images, 40, RenderOutcomeCodes.ImageGenerationFailed, "materials", "materials", true),
            _ => null,
        };
    }

    /// <summary>
    /// <c>lost_task</c>: the row, the plan and the script are removed, so every later <c>GET</c> is 404
    /// <c>adapter_task_unknown</c>.
    /// </summary>
    private async Task LoseTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        _ = await _store.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false);
        _tasks.TryRemove(taskId, out _);
        _frozen.TryRemove(taskId, out _);
        RemoveSubtitle(taskId);
        _planSink.Release(taskId);
    }

    private sealed record FailureShape(
        string Stage,
        int Percent,
        string Code,
        string Detail,
        string FailedStage,
        bool WithImages);

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

    /// <summary>
    /// Releases a task's submitted script once the task is terminal (11.1.2-T1 review fix MOCK-PERF-01): reads
    /// only need the aspect and the SRT path from then on, so the backend does not retain every past script.
    /// </summary>
    private void DropScript(Guid taskId)
    {
        if (_tasks.TryGetValue(taskId, out var submitted) && submitted.Script is not null)
        {
            _tasks[taskId] = submitted with { Script = null };
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

    private sealed record SubmittedTask(string Aspect, string? Script, bool Subtitles);
}
