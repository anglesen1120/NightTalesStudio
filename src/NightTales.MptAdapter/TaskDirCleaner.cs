using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NightTales.MptAdapter;

/// <summary>
/// Removes a task's on-disk footprint at the two moments 05 D15 P4 and D6 allow it: a cancel (at once) and a
/// delete after a terminal state. Three owners are involved and the adapter removes only its own share of the
/// paths — MPT's task dir, the relay's two per-task files (03.4.3-T1 owns their lifecycle) and the adapter's
/// own report dir.
/// </summary>
/// <remarks>
/// Every removal is idempotent: a missing dir or file is the goal, not an error (11.3.5-T2 Failure handling).
/// A path that exists but cannot be removed is logged and does not fail the caller — the MPT record is
/// already gone, and the 24 h backstop of 11.3.4-T1 sweeps what is left.
/// </remarks>
public sealed class TaskDirCleaner
{
    private readonly AdapterOptions _options;
    private readonly ILogger<TaskDirCleaner> _log;

    public TaskDirCleaner(IOptions<AdapterOptions> options, ILogger<TaskDirCleaner> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// Removes <c>&lt;MptRoot&gt;/storage/tasks/&lt;mpt_task_id&gt;</c> (and any dir MPT spelled with a suffix),
    /// <c>&lt;RelayDir&gt;/&lt;task_id&gt;.jsonl</c>, <c>&lt;RelayDir&gt;/plans/&lt;task_id&gt;.json</c> and
    /// <c>&lt;DataDir&gt;/tasks/&lt;task_id&gt;</c>.
    /// </summary>
    public Task CleanAsync(Guid taskId, string? mptTaskId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var task = taskId.ToString("D");
        if (mptTaskId is { Length: > 0 } && SafePath.IsPlainName(mptTaskId))
        {
            RemoveDirectory(Path.Combine(_options.MptTasksDir, mptTaskId));
            RemoveDirectoriesWithPrefix(_options.MptTasksDir, mptTaskId);
        }

        RemoveFile(Path.Combine(_options.RelayDir, $"{task}.jsonl"));
        RemoveFile(Path.Combine(_options.RelayDir, "plans", $"{task}.json"));
        RemoveDirectory(Path.Combine(_options.AdapterTasksDir, task));

        return Task.CompletedTask;
    }

    /// <summary>The task dir MPT created: <c>storage/tasks/&lt;mpt_task_id&gt;</c> (S12: it never GCs one).</summary>
    private void RemoveDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
            {
                return;
            }

            if (info.LinkTarget is not null)
            {
                // A planted link is removed itself, never recursed through (SEC-4).
                info.Delete(recursive: false);
                _log.PathRemoved(path);
                return;
            }

            Directory.Delete(path, recursive: true);
            _log.PathRemoved(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.PathRemoveFailed(path, exception.GetType().Name);
        }
    }

    /// <summary>
    /// 05 C13/S12: MPT's dir is named after the task id and is never garbage collected, so a dir the id only
    /// prefixes is this task's too. A UUID prefix cannot collide with another task.
    /// </summary>
    private void RemoveDirectoriesWithPrefix(string root, string mptTaskId)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var candidate in Directory.EnumerateDirectories(root, $"{mptTaskId}*"))
            {
                if (string.Equals(Path.GetFileName(candidate), mptTaskId, StringComparison.Ordinal))
                {
                    continue;
                }

                RemoveDirectory(candidate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.DirectoryScanFailed(root, exception.GetType().Name);
        }
    }

    /// <summary>The relay's per-task files; missing means 03.4.3-T1 already removed them.</summary>
    private void RemoveFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            File.Delete(path);
            _log.PathRemoved(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.PathRemoveFailed(path, exception.GetType().Name);
        }
    }
}
