using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.MptAdapter;

/// <summary>A parsed <c>render-check.json</c>: the typed view plus the verbatim block the status carries as <c>check</c>.</summary>
internal sealed record CheckReport(RenderCheckV1 Report, JsonElement Raw);

/// <summary>
/// Reads <c>&lt;task dir&gt;/render-check.json</c> through <see cref="RenderNodeJson.Tolerant"/> (an additive
/// report change never breaks the adapter, 11 D4) and copies it under the adapter's own dir so it survives
/// the MPT dir being removed (T3 design). The report is stored verbatim: the adapter never interprets
/// <c>errors[]</c> (05 D10).
/// </summary>
internal sealed class CheckReportReader
{
    private readonly AdapterOptions _options;
    private readonly ILogger<CheckReportReader> _log;

    public CheckReportReader(IOptions<AdapterOptions> options, ILogger<CheckReportReader> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _options = options.Value;
        _log = log;
    }

    /// <summary>The durable copy's path: <c>&lt;DataDir&gt;/tasks/&lt;task_id&gt;/render-check.json</c>.</summary>
    internal string DurableCopyPath(Guid taskId) =>
        Path.Combine(_options.AdapterTasksDir, taskId.ToString("D"), CheckRunner.ReportFileName);

    /// <summary>The report, or null when it is missing, oversized, outside the task dir or not a valid report.</summary>
    internal async Task<CheckReport?> ReadAsync(
        Guid taskId,
        string dirName,
        CancellationToken cancellationToken)
    {
        var opened = SafePath.OpenRead(
            _options.MptTasksDir,
            dirName,
            CheckRunner.ReportFileName,
            _options.MaxReportBytes,
            out var stream);
        if (opened != SafePathResult.Ok || stream is null)
        {
            return null;
        }

        byte[] bytes;
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        // The raw bytes reach the durable path before any parse, so a malformed report is still kept
        // byte-identical for diagnosis; only their sha256 is ever logged (CR-3, S7).
        await SafePath.WriteAtomicAsync(DurableCopyPath(taskId), bytes, cancellationToken).ConfigureAwait(false);

        try
        {
            var typed = JsonSerializer.Deserialize<RenderCheckV1>(bytes, RenderNodeJson.Tolerant);
            if (typed is null)
            {
                _log.ReportUnparseable(taskId, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                return null;
            }

            using var document = JsonDocument.Parse(bytes);
            return new CheckReport(typed, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            _log.ReportUnparseable(taskId, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            return null;
        }
    }
}
