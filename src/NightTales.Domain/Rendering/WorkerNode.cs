using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Domain.Rendering;

/// <summary>
/// One render node of the platform (00 overview §6 L311 <c>worker_nodes</c>, 11 README D13): the row
/// <c>WorkerNodeSeeder</c> upserts from <c>RenderNodes:Nodes</c>, <c>NodeHealthPoller</c> heartbeats, and
/// the operator drains or activates with <c>nts-worker nodes set-state</c>.
/// </summary>
/// <remarks>
/// <para>
/// It is deliberately not <see cref="IUserOwned"/>: nodes are system data, so neither 09 README D10's
/// tenant filter nor an <c>IgnoreQueryFilters</c> allow-list entry applies (09 D10).
/// </para>
/// <para>
/// The state machine it enforces is 11 README F12: only the health poller writes <see cref="WorkerNodeState.Down"/>
/// (after <c>RenderNodes:DownAfterSeconds</c> without a valid answer) and <see cref="WorkerNodeState.Active"/>
/// (after two consecutive valid answers); <see cref="WorkerNodeState.Draining"/> is operator-only and health
/// never clears it, because a drain is a deploy precondition (D12).
/// </para>
/// <para>
/// <c>api_key_ciphertext</c>/<c>api_key_hint</c> are part of the table but have no writer in P (11 README F09/P4):
/// the adapter key comes from the secret manager (<c>RENDER_NODE_&lt;NAME&gt;_API_KEY</c>) and the S settings UI
/// (09.10) becomes the writer. The domain therefore exposes them read-only.
/// </para>
/// </remarks>
public sealed class WorkerNode : Entity, IAuditable
{
    /// <summary>The longest accepted <see cref="Name"/> (the column's <c>HasMaxLength(64)</c>).</summary>
    public const int NameMaxLength = 64;

    /// <summary>The shortest accepted <see cref="Capacity"/> — 05 README L138's <c>NTS_MPT_MAX_CONCURRENT</c> lower bound.</summary>
    public const int CapacityMin = 1;

    /// <summary>The largest accepted <see cref="Capacity"/> — 05 README L138's upper bound [INFERENCE: plan §10 open item 7].</summary>
    public const int CapacityMax = 8;

    /// <summary>The length of the pinned certificate fingerprint: hex, no separators and no colons (D11).</summary>
    public const int TlsCertSha256Length = 64;

    /// <summary>The longest accepted <see cref="GpuModel"/> (the column's <c>HasMaxLength(64)</c>).</summary>
    public const int GpuModelMaxLength = 64;

    /// <summary>The seed value's non-empty name (over 64 characters or blank).</summary>
    public const string NameInvalidCode = "worker_node_name_invalid";

    /// <summary>The base URL is not an absolute <c>http</c>/<c>https</c> URL.</summary>
    public const string BaseUrlInvalidCode = "worker_node_base_url_invalid";

    /// <summary>The capacity is outside <see cref="CapacityMin"/>–<see cref="CapacityMax"/>.</summary>
    public const string CapacityInvalidCode = "worker_node_capacity_invalid";

    /// <summary>The pinned certificate is neither <c>null</c> for an <c>http</c> node nor 64 hexadecimal characters.</summary>
    public const string TlsCertInvalidCode = "worker_node_tls_cert_invalid";

    /// <summary>The GPU model is longer than <see cref="GpuModelMaxLength"/> characters.</summary>
    public const string GpuModelInvalidCode = "worker_node_gpu_model_invalid";

    /// <summary>The operator asked for a state that only health may write (11 README D13, <c>nodes set-state down</c>).</summary>
    public const string OperatorStateInvalidCode = "worker_node_operator_state_invalid";

    private WorkerNode()
    {
    }

    /// <summary>The node's natural key; the unique index and the secret-manager variable name use it.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>The node's <c>mpt-adapter</c> base URL; <c>https</c> outside <c>Development</c> (11 README F26).</summary>
    public Uri BaseUrl { get; private set; } = null!;

    /// <summary>The S settings UI's encrypted adapter key, or <c>null</c> in P (11 README F09).</summary>
    public byte[]? ApiKeyCiphertext { get; private set; }

    /// <summary>The last characters of the stored key as a display hint, or <c>null</c> in P (F09).</summary>
    public string? ApiKeyHint { get; private set; }

    /// <summary>How many renders the node accepts at once, 1–8 (00 §6, 05 README L138).</summary>
    public int Capacity { get; private set; }

    /// <summary>Who may send work to the node (see <see cref="WorkerNodeState"/>).</summary>
    public WorkerNodeState State { get; private set; }

    /// <summary>The instant of the last valid health answer, from the Worker's clock, or <c>null</c> before the first.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    /// <summary>The GPU reported by the node's provisioning, or <c>null</c>.</summary>
    public string? GpuModel { get; private set; }

    /// <summary>
    /// The pinned SHA-256 of the adapter's DER certificate, 64 upper-case hex characters (D11, E6), or
    /// <c>null</c> only for an <c>http://</c> node accepted under <c>Development</c> (the D14 <c>mock</c>
    /// node, F26/T39).
    /// </summary>
    public string? TlsCertSha256 { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Registers a node from configuration (the seeder's insert path, 11 README D13/UC4). The new row is
    /// <see cref="WorkerNodeState.Active"/> so the first health pull decides whether it stays there.
    /// </summary>
    /// <param name="name">The natural key; non-empty, at most <see cref="NameMaxLength"/> characters after trimming.</param>
    /// <param name="baseUrl">The adapter's absolute <c>http</c>/<c>https</c> base URL.</param>
    /// <param name="capacity">How many renders the node accepts at once, <see cref="CapacityMin"/>–<see cref="CapacityMax"/>.</param>
    /// <param name="tlsCertSha256">
    /// The pinned certificate fingerprint; 64 hexadecimal characters (any case, normalised to upper), or
    /// <c>null</c> for an <c>http://</c> node.
    /// </param>
    /// <param name="gpuModel">The node's GPU as provisioning recorded it, or <c>null</c>.</param>
    /// <returns>The new aggregate, tracked by the caller's context.</returns>
    public static ErrorOr<WorkerNode> Create(
        string? name,
        Uri? baseUrl,
        int capacity,
        string? tlsCertSha256,
        string? gpuModel)
    {
        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName) || trimmedName.Length > NameMaxLength)
        {
            return Error.Validation(
                NameInvalidCode,
                $"The render node name must be 1-{NameMaxLength} characters.");
        }

        if (Diagnose(baseUrl, capacity, tlsCertSha256, gpuModel) is { } failure)
        {
            return failure;
        }

        return new WorkerNode
        {
            Id = Guid.CreateVersion7(),
            Name = trimmedName,
            BaseUrl = baseUrl!,
            Capacity = capacity,
            State = WorkerNodeState.Active,
            TlsCertSha256 = NormalisePin(tlsCertSha256),
            GpuModel = string.IsNullOrWhiteSpace(gpuModel) ? null : gpuModel.Trim(),
        };
    }

    /// <summary>
    /// Applies the values the seeder binds from <c>RenderNodes:Nodes</c> to an existing row (the upsert
    /// path): the URL, the capacity, the pin and the GPU model. The state and the last heartbeat are
    /// deliberately untouched, so a rolling restart cannot un-drain a node or fake a fresh answer.
    /// </summary>
    /// <param name="baseUrl">The adapter's absolute <c>http</c>/<c>https</c> base URL.</param>
    /// <param name="capacity">How many renders the node accepts at once.</param>
    /// <param name="tlsCertSha256">The pinned fingerprint, or <c>null</c> for an <c>http://</c> node.</param>
    /// <param name="gpuModel">The node's GPU, or <c>null</c>.</param>
    /// <returns><c>Success</c>, or the validation error of the first rule that fails.</returns>
    public ErrorOr<Success> ApplyConfig(Uri? baseUrl, int capacity, string? tlsCertSha256, string? gpuModel)
    {
        if (Diagnose(baseUrl, capacity, tlsCertSha256, gpuModel) is { } failure)
        {
            return failure;
        }

        BaseUrl = baseUrl!;
        Capacity = capacity;
        TlsCertSha256 = NormalisePin(tlsCertSha256);
        GpuModel = string.IsNullOrWhiteSpace(gpuModel) ? null : gpuModel.Trim();

        return Result.Success;
    }

    /// <summary>
    /// Records a valid health answer (the poller's heartbeat, 11 README D13: any HTTP 200 <em>or</em> 503
    /// with a parseable body). The value is monotonic: a stale or skewed <paramref name="at"/> never moves
    /// <see cref="LastHeartbeatAt"/> backwards (09 README D3 — the Worker's clock is the only reference).
    /// </summary>
    /// <param name="at">The instant of the answer, from the Worker's clock.</param>
    /// <returns>Always <c>Success</c>: a rejected answer is recorded by leaving the value alone.</returns>
    public ErrorOr<Success> RecordHeartbeat(DateTimeOffset at)
    {
        if (LastHeartbeatAt is not { } last || at > last)
        {
            LastHeartbeatAt = at;
        }

        return Result.Success;
    }

    /// <summary>
    /// Marks the row <see cref="WorkerNodeState.Down"/>: the health poller's decision after
    /// <c>RenderNodes:DownAfterSeconds</c> without a valid answer, and the seeder's when a row is no longer
    /// present in <c>RenderNodes:Nodes</c> (11 README D13/F12, UC10).
    /// </summary>
    /// <remarks>
    /// It sets the state from <em>any</em> state, because a node removed from configuration must read
    /// <c>down</c> even if an operator had drained it before the removal. The poller therefore never calls it
    /// for a <see cref="WorkerNodeState.Draining"/> node — health does not end a drain (D12).
    /// </remarks>
    /// <param name="at">The instant the caller decided, from the caller's clock.</param>
    /// <returns>Always <c>Success</c>.</returns>
    public ErrorOr<Success> MarkDown(DateTimeOffset at)
    {
        State = WorkerNodeState.Down;
        UpdatedAt = at;

        return Result.Success;
    }

    /// <summary>
    /// Returns the node to <see cref="WorkerNodeState.Active"/> after two consecutive valid answers
    /// (poller only, 11 README F12's hysteresis against flapping).
    /// </summary>
    /// <remarks>
    /// It is a no-op <c>Success</c> from <see cref="WorkerNodeState.Draining"/> — health does not cancel a
    /// drain — and from <see cref="WorkerNodeState.Active"/>, so a steady node raises nothing.
    /// </remarks>
    /// <param name="at">The instant of the second good pull, from the Worker's clock.</param>
    /// <returns>Always <c>Success</c>.</returns>
    public ErrorOr<Success> MarkActive(DateTimeOffset at)
    {
        if (State is WorkerNodeState.Draining or WorkerNodeState.Active)
        {
            return Result.Success;
        }

        State = WorkerNodeState.Active;
        UpdatedAt = at;

        return Result.Success;
    }

    /// <summary>
    /// Applies an operator state (the <c>nts-worker nodes set-state</c> verbs, 11 README D13).
    /// </summary>
    /// <remarks>
    /// Only <see cref="WorkerNodeState.Active"/> and <see cref="WorkerNodeState.Draining"/> are accepted:
    /// <see cref="WorkerNodeState.Down"/> is health's decision alone, and the CLI maps this refusal to
    /// usage exit 2 rather than inventing an error code (plan §5). Setting the state the node already has
    /// is an idempotent <c>Success</c>.
    /// </remarks>
    /// <param name="state">The requested operator state.</param>
    /// <returns><c>Success</c>, or the refusal that becomes usage exit 2.</returns>
    public ErrorOr<Success> SetOperatorState(WorkerNodeState state)
    {
        if (state == WorkerNodeState.Down)
        {
            return Error.Validation(
                OperatorStateInvalidCode,
                "An operator may set 'active' or 'draining' only; 'down' is decided by health.");
        }

        State = state;

        return Result.Success;
    }

    /// <summary>
    /// The shape rules <see cref="Create"/> and <see cref="ApplyConfig"/> share: an absolute
    /// <c>http</c>/<c>https</c> URL without credentials, a capacity in range, a pin that is <c>null</c>
    /// exactly for an <c>http://</c> URL and otherwise 64 hexadecimal characters, and a bounded GPU model.
    /// </summary>
    private static Error? Diagnose(Uri? baseUrl, int capacity, string? tlsCertSha256, string? gpuModel)
    {
        if (baseUrl is null || !baseUrl.IsAbsoluteUri
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            return Error.Validation(BaseUrlInvalidCode, "The render node base URL must be an absolute http or https URL.");
        }

        // Credentials in the authority (`user:pass@host`) are refused: the adapter key is the
        // `x-api-key` header, and a URL that carries a secret would be echoed by `nodes list` (plan §5:
        // no key, hint or ciphertext on stdout).
        if (baseUrl.UserInfo.Length > 0)
        {
            return Error.Validation(
                BaseUrlInvalidCode,
                "The render node base URL must not carry credentials (user:password@).");
        }

        if (capacity is < CapacityMin or > CapacityMax)
        {
            return Error.Validation(
                CapacityInvalidCode,
                $"The render node capacity must be between {CapacityMin} and {CapacityMax}.");
        }

        if (tlsCertSha256 is null)
        {
            // Mirrors ck_worker_nodes_tls_sha256: a row without a pin is a plain-URL node (the D14 `mock`
            // node), which 11 README F26 allows under Development only — RenderNodeConnectionFactory is
            // the authority that refuses it everywhere else.
            if (baseUrl.Scheme != Uri.UriSchemeHttp)
            {
                return Error.Validation(
                    TlsCertInvalidCode,
                    "An https render node must carry the pinned certificate fingerprint (64 hexadecimal characters).");
            }
        }
        else if (!IsHex64(tlsCertSha256))
        {
            return Error.Validation(
                TlsCertInvalidCode,
                $"The pinned certificate fingerprint must be exactly {TlsCertSha256Length} hexadecimal characters with no separators.");
        }

        if (gpuModel is { } model && model.Trim().Length > GpuModelMaxLength)
        {
            return Error.Validation(
                GpuModelInvalidCode,
                $"The render node GPU model must be at most {GpuModelMaxLength} characters.");
        }

        return null;
    }

    /// <summary>Normalises a fingerprint to the stored form: upper-case hex, no separators (D11).</summary>
    private static string? NormalisePin(string? tlsCertSha256) =>
        tlsCertSha256 is null ? null : tlsCertSha256.Trim().ToUpperInvariant();

    private static bool IsHex64(string value) =>
        value.Length == TlsCertSha256Length && value.All(character => Uri.IsHexDigit(character));
}
