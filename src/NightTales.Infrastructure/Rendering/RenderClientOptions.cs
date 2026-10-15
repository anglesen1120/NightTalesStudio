namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The <c>Render</c> configuration section (09 README BD6, WF08/W9): the timeouts, the retry budget and the
/// circuit-breaker thresholds of the two render-node clients, the download's idle timer and total budget, and
/// the two byte caps. Bound and validated when the host starts, never lazily on the first render.
/// </summary>
/// <remarks>
/// <para>
/// Every value here is infrastructure-side plumbing: none of them contains a node key, a base URL or any
/// other secret, so this object is safe in a diagnostic dump (the key lives in the secret manager and is read
/// per node by <c>RenderNodeConnectionFactory</c>, 09 README BD16/S4).
/// </para>
/// <para>
/// The section also carries the keys of the other 09.6 tasks (<c>Render:PollSeconds</c>,
/// <c>Render:MaxMinutes</c>, …); the binder ignores the members it does not see here, so this type owns only
/// the client half.
/// </para>
/// <para>
/// [INFERENCE — 09.6.2-T1 task §Design] the task text writes <c>Render:Download.IdleSeconds</c>; a nested
/// <see cref="Download"/> object binds <c>Render:Download:IdleSeconds</c>, which is the key documented on
/// <see cref="DownloadOptions.IdleSeconds"/> and the one a host configures.
/// </para>
/// </remarks>
public sealed class RenderClientOptions
{
    /// <summary>The configuration section the render tasks bind.</summary>
    public const string SectionName = "Render";

    /// <summary>The timeouts, the retry budget and the circuit breaker of the retrying node client.</summary>
    public ClientOptions Client { get; init; } = new();

    /// <summary>The idle timer of the streaming download client.</summary>
    public DownloadOptions Download { get; init; } = new();

    /// <summary>
    /// The total budget of one download, in minutes (<c>Render:DownloadMaxMinutes</c>, 20): a generous
    /// ceiling that must not kill a legitimate large file, since a 2 GiB video over a slow node link takes
    /// minutes (WF08). The 60 s idle timer is what detects a stalled stream.
    /// </summary>
    public int DownloadMaxMinutes { get; init; } = 20;

    /// <summary>The cap of a video download in bytes (<c>Render:MaxVideoBytes</c>, 2 GiB, BD6).</summary>
    public long MaxVideoBytes { get; init; } = 2_147_483_648L;

    /// <summary>The cap of a subtitle download in bytes (<c>Render:MaxSubtitleBytes</c>, 2 MiB, BD6).</summary>
    public long MaxSubtitleBytes { get; init; } = 2_097_152L;

    /// <summary>
    /// The <c>Render:Client</c> object: the timeouts, the retry budget and the circuit breaker of the client
    /// that talks to a node (never the standard resilience handler's defaults, W9).
    /// </summary>
    public sealed class ClientOptions
    {
        /// <summary>
        /// The attempt timeout in seconds (<c>Render:Client:AttemptSeconds</c>, 10): one call against one node.
        /// </summary>
        public int AttemptSeconds { get; init; } = 10;

        /// <summary>
        /// The total timeout in seconds (<c>Render:Client:TotalSeconds</c>, 30) covering every attempt of one
        /// logical call (WF08): a render dispatch may not occupy the Worker longer than this.
        /// </summary>
        public int TotalSeconds { get; init; } = 30;

        /// <summary>
        /// How many times a retryable call is repeated (<c>Render:Client:MaxRetries</c>, 3). Zero is legal and
        /// disables retrying; the DELETE call never retries whatever this says (WT55).
        /// </summary>
        public int MaxRetries { get; init; } = 3;

        /// <summary>The circuit breaker of the node client (W9: it must never open at P volume by accident).</summary>
        public CircuitBreakerOptions CircuitBreaker { get; init; } = new();
    }

    /// <summary>The <c>Render:Client:CircuitBreaker</c> object (09 README W9).</summary>
    public sealed class CircuitBreakerOptions
    {
        /// <summary>
        /// The fewest calls in the sampling window before the ratio is judged
        /// (<c>Render:Client:CircuitBreaker:MinimumThroughput</c>, 5).
        /// </summary>
        public int MinimumThroughput { get; init; } = 5;

        /// <summary>
        /// The failure ratio that opens the breaker (<c>Render:Client:CircuitBreaker:FailureRatio</c>, 1.0:
        /// only a window in which nothing succeeded opens it).
        /// </summary>
        public double FailureRatio { get; init; } = 1.0;

        /// <summary>
        /// The window the ratio is measured over, in seconds
        /// (<c>Render:Client:CircuitBreaker:SamplingDurationSeconds</c>, 60).
        /// </summary>
        public int SamplingDurationSeconds { get; init; } = 60;

        /// <summary>
        /// How long the breaker stays open before it half-opens, in seconds
        /// (<c>Render:Client:CircuitBreaker:BreakDurationSeconds</c>, 30).
        /// </summary>
        public int BreakDurationSeconds { get; init; } = 30;
    }

    /// <summary>The <c>Render:Download</c> object: the streaming download client's own timers.</summary>
    public sealed class DownloadOptions
    {
        /// <summary>
        /// How long the read may stall before it is aborted, in seconds
        /// (<c>Render:Download:IdleSeconds</c>, 60). The client's HTTP timeout is infinite, so this timer —
        /// not a total timeout — is what ends a stalled node (WF08).
        /// </summary>
        public int IdleSeconds { get; init; } = 60;
    }
}
