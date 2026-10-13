using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Research.Facts;

namespace NightTales.TestKit.Research;

/// <summary>
/// The scripted <see cref="IFactExtractor"/> of every test that must not reach a provider: outcomes are queued and
/// consumed in order, the calls are counted and every request is recorded. A call with nothing queued fails loudly
/// instead of inventing facts, so a test that forgot to script the extraction cannot pass by accident.
/// </summary>
/// <remarks>
/// The queue and the recording are guarded, because the fact-sheet job extracts on a job thread while a test may read
/// <see cref="Requests"/> or <see cref="Calls"/> from its own thread.
/// </remarks>
public sealed class FakeFactExtractor : IFactExtractor
{
    /// <summary>The model every fake outcome names.</summary>
    public const string Model = "gpt-6-luna";

    /// <summary>The rule id of an invalid output that named none.</summary>
    private static readonly IReadOnlyList<string> DefaultRuleIds = ["schema_invalid"];

    /// <summary>The category of a refusal that named none.</summary>
    private static readonly IReadOnlyList<string> DefaultCategories = ["content_filter"];

    private readonly object _gate = new();
    private readonly Queue<TextCallResult<FactsV1>> _script = new();
    private readonly List<FactExtractionRequest> _requests = [];

    /// <summary>How many extraction calls were made.</summary>
    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    /// <summary>Every request received, in order; a snapshot, so the caller may read it while the job runs.</summary>
    public IReadOnlyList<FactExtractionRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Queues the outcome of the next extraction call.</summary>
    /// <param name="result">The outcome.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeFactExtractor Enqueue(TextCallResult<FactsV1> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            _script.Enqueue(result);
        }

        return this;
    }

    /// <summary>Queues a successful extraction.</summary>
    /// <param name="facts">The parsed <c>facts.v1</c> output.</param>
    /// <param name="usage">The reported usage, or <see langword="null"/> for none.</param>
    /// <param name="requestId">The provider request id, or <see langword="null"/> for none.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeFactExtractor EnqueueOk(FactsV1 facts, TokenUsage? usage = null, string? requestId = "req-fake")
    {
        ArgumentNullException.ThrowIfNull(facts);

        return Enqueue(TextCallResult<FactsV1>.Ok(facts, Model, usage, requestId));
    }

    /// <summary>Queues an invalid output (<c>schema_invalid</c>/<c>max_output_tokens_exceeded</c>): the D7 retry path.</summary>
    /// <param name="ruleIds">The validator rule ids the provider reported.</param>
    /// <param name="usage">The reported usage, or <see langword="null"/> for none.</param>
    /// <param name="requestId">The provider request id, or <see langword="null"/> for none.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeFactExtractor EnqueueInvalidOutput(
        IReadOnlyList<string>? ruleIds = null,
        TokenUsage? usage = null,
        string? requestId = null) =>
        Enqueue(TextCallResult<FactsV1>.InvalidOutput(Model, usage, requestId, ruleIds ?? DefaultRuleIds));

    /// <summary>Queues a refusal or a content filter: the run fails <c>fact_sheet_moderated</c>.</summary>
    /// <param name="categories">The provider's categories.</param>
    /// <param name="usage">The reported usage, or <see langword="null"/> for none.</param>
    /// <param name="requestId">The provider request id, or <see langword="null"/> for none.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeFactExtractor EnqueueModerated(
        IReadOnlyList<string>? categories = null,
        TokenUsage? usage = null,
        string? requestId = null) =>
        Enqueue(TextCallResult<FactsV1>.Moderated(Model, usage, requestId, categories ?? DefaultCategories));

    /// <summary>Queues a funds-exhausted outcome: the run fails <c>provider_funds_exhausted</c> and is never metered.</summary>
    /// <param name="requestId">The provider request id, or <see langword="null"/> for none.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeFactExtractor EnqueueFundsExhausted(string? requestId = null) =>
        Enqueue(TextCallResult<FactsV1>.ProviderFundsExhausted(Model, requestId));

    /// <summary>Queues a definitive provider rejection: the run fails <c>fact_sheet_generation_failed</c>.</summary>
    /// <param name="status">The HTTP status the provider answered.</param>
    /// <param name="requestId">The provider request id, or <see langword="null"/> for none.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeFactExtractor EnqueueProviderError(int status = 400, string? requestId = null) =>
        Enqueue(TextCallResult<FactsV1>.ProviderError(Model, status, requestId));

    /// <summary>Queues a timeout or a 5xx past the retry budget: the run fails <c>fact_sheet_generation_failed</c>.</summary>
    /// <param name="requestId">The provider request id, or <see langword="null"/> for none.</param>
    /// <param name="usage">The usage a failed response still reported, or <see langword="null"/> for none.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeFactExtractor EnqueueUnavailable(string? requestId = null, TokenUsage? usage = null) =>
        Enqueue(TextCallResult<FactsV1>.ProviderUnavailable(Model, requestId, usage));

    /// <inheritdoc />
    public ValueTask<TextCallResult<FactsV1>> ExtractAsync(FactExtractionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        lock (_gate)
        {
            _requests.Add(req);

            if (_script.Count == 0)
            {
                throw new InvalidOperationException("FakeFactExtractor has no scripted outcome left.");
            }

            return ValueTask.FromResult(_script.Dequeue());
        }
    }
}
