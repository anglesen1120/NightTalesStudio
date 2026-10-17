using NightTales.Application.Abstractions.Text;

namespace NightTales.TestKit.Text;

/// <summary>
/// The scripted <see cref="ITextClient"/> of every test that must not reach a provider (02 README §tests):
/// outcomes are queued per step and consumed in order, and every request is recorded. A call with nothing
/// queued fails loudly instead of inventing an answer.
/// </summary>
public sealed class FakeTextClient : ITextClient
{
    /// <summary>The model every fake outcome names.</summary>
    public const string Model = "gpt-6-luna";

    private readonly Dictionary<TextStep, Queue<object>> _scripts = [];
    private readonly List<TextCallRequest> _requests = [];

    /// <summary>Every request received, in order.</summary>
    public IReadOnlyList<TextCallRequest> Requests => _requests;

    /// <summary>How many requests of one step the fake has received.</summary>
    /// <param name="step">The step to count.</param>
    /// <returns>The number of calls of that step.</returns>
    public int Calls(TextStep step) => _requests.Count(request => request.Step == step);

    /// <summary>How many outcomes are still queued for one step, i.e. how many of its calls never happened.</summary>
    /// <param name="step">The step to inspect.</param>
    /// <returns>The number of unconsumed outcomes of that step.</returns>
    public int Queued(TextStep step) => _scripts.TryGetValue(step, out var queue) ? queue.Count : 0;

    /// <summary>Queues the outcome of the next call of <paramref name="step"/>.</summary>
    /// <typeparam name="T">The output document type the call asks for.</typeparam>
    /// <param name="step">The step the outcome answers.</param>
    /// <param name="result">The outcome.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeTextClient Enqueue<T>(TextStep step, TextCallResult<T> result)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!_scripts.TryGetValue(step, out var queue))
        {
            _scripts[step] = queue = new Queue<object>();
        }

        queue.Enqueue(result);

        return this;
    }

    /// <summary>Queues a successful outcome with the given usage.</summary>
    /// <typeparam name="T">The output document type.</typeparam>
    /// <param name="step">The step.</param>
    /// <param name="draft">The parsed output.</param>
    /// <param name="usage">The reported usage, or <see langword="null"/> for none.</param>
    /// <returns>This fake.</returns>
    public FakeTextClient EnqueueOk<T>(TextStep step, T draft, TokenUsage? usage = null)
        where T : class =>
        Enqueue(step, TextCallResult<T>.Ok(draft, Model, usage, "req-fake"));

    /// <inheritdoc />
    public ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(req);

        _requests.Add(req);

        if (!_scripts.TryGetValue(req.Step, out var queue) || queue.Count == 0)
        {
            throw new InvalidOperationException($"FakeTextClient has no scripted outcome left for step {req.Step}.");
        }

        return ValueTask.FromResult((TextCallResult<T>)queue.Dequeue());
    }
}
