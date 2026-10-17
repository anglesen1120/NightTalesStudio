using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The seam that hands an <c>ingesting</c> render job to the output-ingest step (03.4.2-T1/WF14): the poller
/// calls it once the job's transition to <c>ingesting</c> is committed, never before.
/// </summary>
/// <remarks>
/// <para>
/// 09.6.3-T1 ships only the <see cref="LoggingRenderIngestEnqueuer"/> default; 09.6.3-T2 replaces the
/// registration with a Hangfire enqueue of <c>IngestRenderOutputJob</c>. The poller therefore depends on this
/// seam instead of on the job type, so the split of submit+poll from ingest (WF14) stays a registration
/// change.
/// </para>
/// <para>
/// The id is the only argument (D12, ids-only job arguments): the enqueuer must not carry the job's params,
/// the node or anything a log could echo.
/// </para>
/// </remarks>
public interface IRenderIngestEnqueuer
{
    /// <summary>Queues the output ingest of <paramref name="renderJobId"/>.</summary>
    /// <param name="renderJobId">The committed <c>ingesting</c> render job.</param>
    void Enqueue(Guid renderJobId);
}

/// <summary>
/// The default <see cref="IRenderIngestEnqueuer"/>: it warns that the ingest step is not composed yet and
/// enqueues nothing.
/// </summary>
/// <remarks>
/// Registered with <c>TryAdd</c> so 09.6.3-T2 replaces it. The warning carries the render job id alone — never
/// the node, the key or the params (BD16) — and is written once per job the poller moves to <c>ingesting</c>,
/// which is the visible signal that the ingest job is missing from the host.
/// </remarks>
/// <param name="logger">The host's logger.</param>
public sealed partial class LoggingRenderIngestEnqueuer(ILogger<LoggingRenderIngestEnqueuer> logger)
    : IRenderIngestEnqueuer
{
    /// <inheritdoc />
    public void Enqueue(Guid renderJobId) => LogIngestNotWired(renderJobId);

    [LoggerMessage(
        EventId = 6197,
        Level = LogLevel.Warning,
        Message = "render_ingest_not_wired renderJobId={RenderJobId}")]
    private partial void LogIngestNotWired(Guid renderJobId);
}
