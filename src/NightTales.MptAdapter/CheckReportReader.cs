using System.Text.Json;
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

    public CheckReportReader(IOptions<AdapterOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
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

        CheckReport parsed;
        try
        {
            var typed = JsonSerializer.Deserialize<RenderCheckV1>(bytes, RenderNodeJson.Tolerant);
            if (typed is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(bytes);
            parsed = new CheckReport(typed, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }

        await SafePath.WriteAtomicAsync(DurableCopyPath(taskId), bytes, cancellationToken).ConfigureAwait(false);
        return parsed;
    }
}
