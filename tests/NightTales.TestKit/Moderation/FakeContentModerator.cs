using NightTales.Application.Abstractions.Moderation;

namespace NightTales.TestKit.Moderation;

/// <summary>
/// The scripted <see cref="IContentModerator"/> of every test that must not reach the moderation endpoint (02 README
/// D12): verdicts are queued per stage, a stage with nothing queued answers clean, and every check is recorded.
/// </summary>
public sealed class FakeContentModerator : IContentModerator
{
    private readonly Dictionary<ModerationStage, Queue<Func<ModerationResult>>> _script = [];
    private readonly List<(IReadOnlyList<string> Texts, ModerationStage Stage)> _checks = [];

    /// <summary>The number of checks made.</summary>
    public int Calls => _checks.Count;

    /// <summary>Every check received, in order. The texts are test data only.</summary>
    public IReadOnlyList<(IReadOnlyList<string> Texts, ModerationStage Stage)> Checks => _checks;

    /// <summary>Queues a flagged verdict with <paramref name="categories"/> for the next check of <paramref name="stage"/>.</summary>
    /// <param name="stage">The stage the verdict answers.</param>
    /// <param name="categories">The flagged categories.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeContentModerator EnqueueFlagged(ModerationStage stage, params string[] categories) =>
        Enqueue(
            stage,
            () => new ModerationResult(true, categories, categories.ToDictionary(c => c, _ => 0.99, StringComparer.Ordinal)));

    /// <summary>Queues a clean verdict for the next check of <paramref name="stage"/>.</summary>
    /// <param name="stage">The stage the verdict answers.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeContentModerator EnqueueClean(ModerationStage stage) => Enqueue(stage, () => ModerationResult.Clean);

    /// <summary>Queues a failure (the endpoint is down after the retry budget) for the next check of <paramref name="stage"/>.</summary>
    /// <param name="stage">The stage the failure answers.</param>
    /// <param name="status">The HTTP status, when known.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeContentModerator EnqueueUnavailable(ModerationStage stage, int? status = 503) =>
        Enqueue(stage, () => throw new ModerationUnavailableException(status));

    /// <inheritdoc />
    public ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ct.ThrowIfCancellationRequested();

        _checks.Add((texts, stage));

        var verdict = _script.TryGetValue(stage, out var queue) && queue.Count > 0
            ? queue.Dequeue()()
            : ModerationResult.Clean;

        return ValueTask.FromResult(verdict);
    }

    private FakeContentModerator Enqueue(ModerationStage stage, Func<ModerationResult> verdict)
    {
        if (!_script.TryGetValue(stage, out var queue))
        {
            _script[stage] = queue = new Queue<Func<ModerationResult>>();
        }

        queue.Enqueue(verdict);

        return this;
    }
}
