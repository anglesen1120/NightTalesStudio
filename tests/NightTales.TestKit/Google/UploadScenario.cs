namespace NightTales.TestKit.Google;

/// <summary>
/// The fault switches of <see cref="FakeGoogleServer"/>'s resumable-upload surface (06.2.3-T1), set from a test
/// before the request it is about: <see cref="FakeGoogleServer.Scenario"/> is assigned one of the factories here,
/// everything a test does not set stays off, and a test that never touches the scenario gets Google's happy path.
/// </summary>
/// <remarks>
/// <para>
/// The switches live in one record so a test declares what is wrong instead of reaching into the fake: the fake
/// itself answers from this value only, never from state a test did not configure.
/// </para>
/// <para>
/// The token endpoint's refusals are deliberately <em>not</em> here: <c>invalid_grant</c>,
/// <c>invalid_client</c> and <c>unauthorized_client</c> are declared on
/// <see cref="FakeGoogleOptions.TokenError"/> (with <see cref="FakeGoogleOptions.TokenStatusCode"/>), because the
/// connect tests already drive them there and one fault must not have two homes.
/// </para>
/// </remarks>
public sealed record UploadScenario
{
    /// <summary>No fault: the upload surface behaves like Google's happy path.</summary>
    public static UploadScenario None { get; } = new();

    /// <summary>
    /// Every chunk <c>PUT</c> that declares the end of the object is read and then dropped: no answer, the
    /// connection aborted, and the same for every retry until the test clears the scenario. Probes and
    /// non-final chunks are unaffected — the "connection died after the last chunk" fault
    /// (plan §"Failure handling").
    /// </summary>
    public static UploadScenario DropAfterLastChunk() => new() { DropLastChunk = true };

    /// <summary>Every request to the session answers <c>404</c>: the session expired server-side.</summary>
    public static UploadScenario ExpiredSession() => new() { ExpireSession = true };

    /// <summary>The next <paramref name="chunkRequests"/> chunk requests answer <c>503</c>, then normality resumes.</summary>
    /// <param name="chunkRequests">How many chunk requests fail; the session create is unaffected.</param>
    public static UploadScenario ServerErrors(int chunkRequests) => new() { ChunkServerErrors = chunkRequests };

    /// <summary>
    /// Every chunk request answers <c>503</c>, forever: the caller must stop after its own retry budget, so a
    /// test drives the terminal transport failure without the fake ever recovering (plan §7's retry rule).
    /// </summary>
    public static UploadScenario PersistentServerErrors { get; } = new() { ServerErrorsPersistently = true };

    /// <summary>Any session request answers Google's refusal body for <paramref name="reason"/>.</summary>
    /// <param name="reason">The <c>error.errors[].reason</c>, e.g. <see cref="Reasons.QuotaExceeded"/>.</param>
    public static UploadScenario Refusal(string reason) => new() { UploadFailureReason = reason };

    /// <summary>The daily upload quota is spent: <c>403</c> + <c>quotaExceeded</c>.</summary>
    public static UploadScenario QuotaExceeded() => Refusal(Reasons.QuotaExceeded);

    /// <summary>The channel's upload limit is reached: <c>403</c> + <c>uploadLimitExceeded</c>.</summary>
    public static UploadScenario UploadLimitExceeded() => Refusal(Reasons.UploadLimitExceeded);

    /// <summary>The scheduled publication instant is refused: <c>400</c> + <c>invalidPublishAt</c>.</summary>
    public static UploadScenario InvalidPublishAt() => Refusal(Reasons.InvalidPublishAt);

    /// <summary>
    /// The insert itself is refused as unauthorized: the session create answers <c>401</c> + <c>authError</c>, the
    /// answer Google gives a token it no longer accepts. It is the "refusal after the send" shape: the create has
    /// been issued by the time this arrives, which is exactly what the release predicate has to distinguish from a
    /// refusal before it (06.2.3-T2 YR19, QA D2).
    /// </summary>
    public static UploadScenario SessionCreateAuthFailure() => new() { AuthFailureAtSessionCreate = true };

    /// <summary>A <c>401</c> + <c>authError</c> on one chunk <c>PUT</c> only: the token expired mid-stream shape.</summary>
    /// <param name="chunkRequest">Which chunk request answers 401, counting from 1; 0 turns it off.</param>
    public static UploadScenario AuthErrorOnChunk(int chunkRequest) => new() { AuthErrorChunkRequest = chunkRequest };

    /// <summary>
    /// Accepts <paramref name="acceptedChunks"/> chunk <c>PUT</c>s and then drops every later one, so the session holds
    /// a prefix of the object and the next run resumes from it with a <c>Partial</c> probe (PERF-2).
    /// </summary>
    /// <param name="acceptedChunks">How many chunks are appended before the connection starts dying.</param>
    public static UploadScenario DropChunksAfter(int acceptedChunks) => new() { AcceptChunksBeforeDrop = acceptedChunks };

    /// <summary>Whether the drop-after-last-chunk fault is armed.</summary>
    public bool DropLastChunk { get; init; }

    /// <summary>Which chunk <c>PUT</c> answers <c>401</c> + <c>authError</c> (1-based), or 0 for none (PERF-5).</summary>
    public int AuthErrorChunkRequest { get; init; }

    /// <summary>
    /// How many chunk <c>PUT</c>s are appended before every later one is read and dropped, so the session keeps a
    /// prefix; 0 (the default) never drops (PERF-2).
    /// </summary>
    public int AcceptChunksBeforeDrop { get; init; }

    /// <summary>Whether every session request answers <c>404</c>.</summary>
    public bool ExpireSession { get; init; }

    /// <summary>
    /// Whether the session create answers <c>401</c> + <c>authError</c> instead of opening a session: the insert is
    /// refused after it was issued, so nothing is opened and no chunk can follow.
    /// </summary>
    public bool AuthFailureAtSessionCreate { get; init; }

    /// <summary>The <c>error.errors[].reason</c> every session request is refused with, or null for no refusal.</summary>
    public string? UploadFailureReason { get; init; }

    /// <summary>How many chunk requests answer <c>503</c> after the scenario was assigned.</summary>
    public int ChunkServerErrors { get; init; }

    /// <summary>Whether every chunk request answers <c>503</c>.</summary>
    public bool ServerErrorsPersistently { get; init; }

    /// <summary>
    /// How many bytes a chunk <c>PUT</c> is allowed to append, whatever it declared; 0 (the default) appends the
    /// whole body. <c>1</c> is the hostile-peer shape: every answer is a <c>308</c> whose <c>Range</c> moved one
    /// byte further, so a client without a per-chunk pass budget would issue one request per byte (SEC-5).
    /// </summary>
    public int ChunkProgressBytes { get; init; }

    /// <summary>The same scenario with the drop-after-last-chunk fault set as asked.</summary>
    /// <param name="armed">False clears it.</param>
    public UploadScenario WithDropLastChunk(bool armed = true) => this with { DropLastChunk = armed };

    /// <summary>The same scenario with the session-expiry fault set as asked.</summary>
    /// <param name="armed">False clears it, so a test can expire a session and then let it work again.</param>
    public UploadScenario WithExpiredSession(bool armed = true) => this with { ExpireSession = armed };

    /// <summary>The same scenario with a refusal reason set.</summary>
    /// <param name="reason">The <c>error.errors[].reason</c>; null clears any refusal.</param>
    public UploadScenario WithRefusal(string? reason) => this with { UploadFailureReason = reason };

    /// <summary>The same scenario with a bounded run of <c>503</c> chunk answers.</summary>
    /// <param name="chunkRequests">How many chunk requests fail from here on.</param>
    public UploadScenario WithServerErrors(int chunkRequests) =>
        this with { ChunkServerErrors = chunkRequests, ServerErrorsPersistently = false };

    /// <summary>The same scenario with every chunk request answering <c>503</c>.</summary>
    public UploadScenario WithPersistentServerErrors() =>
        this with { ServerErrorsPersistently = true, ChunkServerErrors = 0 };

    /// <summary>The same scenario with the mid-stream <c>401</c> armed on the given chunk request (PERF-5).</summary>
    /// <param name="chunkRequest">The 1-based chunk request, or 0 to disarm.</param>
    public UploadScenario WithAuthErrorOnChunk(int chunkRequest) => this with { AuthErrorChunkRequest = chunkRequest };

    /// <summary>The same scenario dropping every chunk after the first <paramref name="accepted"/> ones (PERF-2).</summary>
    /// <param name="accepted">How many chunks the session keeps.</param>
    public UploadScenario WithDropChunksAfter(int accepted) => this with { AcceptChunksBeforeDrop = accepted };

    /// <summary>The same scenario with a bounded per-request append, 0 to append the whole body (SEC-5).</summary>
    /// <param name="bytes">How many bytes a chunk <c>PUT</c> may append.</param>
    public UploadScenario WithChunkProgressBytes(int bytes) => this with { ChunkProgressBytes = bytes };

    /// <summary>Google's <c>error.errors[].reason</c> values the upload surface refuses with.</summary>
    public static class Reasons
    {
        /// <summary>The daily quota is spent.</summary>
        public const string QuotaExceeded = "quotaExceeded";

        /// <summary>The channel cannot upload again today.</summary>
        public const string UploadLimitExceeded = "uploadLimitExceeded";

        /// <summary>The publication instant is not accepted (a channel-schedule constraint, plan §3).</summary>
        public const string InvalidPublishAt = "invalidPublishAt";

        /// <summary>
        /// The token is no longer accepted: Google's <c>401</c> reason. The client maps it to
        /// <c>youtube_reauth_required</c>, never to a transport retry.
        /// </summary>
        public const string AuthError = "authError";
    }
}
