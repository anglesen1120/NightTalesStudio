using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter;

/// <summary>
/// The output half of <see cref="IRenderBackend"/> (11.3.5-T3). The path is built from the stored
/// <c>backend_task_id</c> and a constant file name - never from the request (S8) - and goes through
/// <see cref="SafePath"/>. Length and <c>X-Content-SHA256</c> are the published values from the check; the
/// file is opened as a stream and never read or re-hashed here (05 D15 P3, Q3).
/// </summary>
public sealed class FileOutputSource
{
    internal const string VideoContentType = "video/mp4";
    internal const string SubtitlesContentType = "application/x-subrip; charset=utf-8";

    private readonly ITaskStore _store;
    private readonly AdapterOptions _options;
    private readonly ILogger<FileOutputSource> _log;

    public FileOutputSource(ITaskStore store, IOptions<AdapterOptions> options, ILogger<FileOutputSource> log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _options = options.Value;
        _log = log;
    }

    public async Task<ErrorOr<OutputFile>> OpenAsync(Guid taskId, OutputKind kind, CancellationToken cancellationToken)
    {
        var row = await _store.TryGetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Absent();
        }

        if (!string.Equals(row.State, RenderStates.Succeeded, StringComparison.Ordinal) || row.Output is null)
        {
            return Error.Conflict(AdapterErrorCodes.OutputNotReady, "The task has no published output yet.");
        }

        var published = kind == OutputKind.Video ? row.Output.Video : row.Output.Subtitles;
        if (row.CleanedAt is not null || published is null || string.IsNullOrEmpty(row.BackendTaskId))
        {
            return Absent();
        }

        var fileName = kind == OutputKind.Video ? OutputPublisher.VideoFileName : OutputPublisher.SubtitlesFileName;
        var maxBytes = kind == OutputKind.Video ? _options.MaxVideoBytes : _options.MaxSubtitleBytes;
        var opened = SafePath.OpenRead(_options.MptTasksDir, row.BackendTaskId, fileName, maxBytes, out var stream);
        if (opened != SafePathResult.Ok || stream is null)
        {
            _log.OutputUnavailable(taskId, kind, opened.ToString());
            return Absent();
        }

        // The published length is what the Worker verifies; a file that no longer has it was replaced.
        if (stream.Length != published.Bytes)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            _log.OutputUnavailable(taskId, kind, "length_mismatch");
            return Absent();
        }

        var contentType = kind == OutputKind.Video ? VideoContentType : SubtitlesContentType;
        return new OutputFile(stream, published.Bytes, published.Sha256, contentType);
    }

    private static Error Absent() =>
        Error.NotFound(AdapterErrorCodes.OutputAbsent, "The output is not available.");
}
