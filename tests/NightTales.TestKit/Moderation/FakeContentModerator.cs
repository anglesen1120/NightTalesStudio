using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Text;

namespace NightTales.TestKit.Moderation;

/// <summary>
/// The scripted <see cref="IContentModerator"/> of every test that must not reach the moderation endpoint (02 README
/// D12): verdicts are queued per stage, a stage with nothing queued and no matcher that fits answers clean, and
/// every check is recorded.
/// </summary>
/// <remarks>
/// Three seams, in the order they answer: a queued verdict (<see cref="EnqueueFlagged"/>, <see cref="EnqueueClean"/>,
/// <see cref="EnqueueScores"/>, <see cref="EnqueueUnavailable"/>), then a text matcher
/// (<see cref="EnqueueFlaggedWhen"/>) for the callers that send one batch and then re-check chunk by chunk, then
/// <see cref="ModerationResult.Clean"/>.
/// </remarks>
public sealed class FakeContentModerator : IContentModerator
{
    private readonly Dictionary<ModerationStage, Queue<Func<ModerationResult>>> _script = [];
    private readonly Dictionary<ModerationStage, List<(Func<IReadOnlyList<string>, bool> Matches, Func<ModerationResult> Verdict)>> _matchers = [];
    private readonly List<(IReadOnlyList<string> Texts, ModerationStage Stage)> _checks = [];
    private readonly List<RunContext?> _scopes = [];

    /// <summary>The number of checks made.</summary>
    public int Calls => _checks.Count;

    /// <summary>Every check received, in order. The texts are test data only.</summary>
    public IReadOnlyList<(IReadOnlyList<string> Texts, ModerationStage Stage)> Checks => _checks;

    /// <summary>
    /// The <see cref="ModerationScope"/> in force at every check, in the same order: null when the caller moderated
    /// outside a run scope, so the call would not have been metered (02.8.2-T2 Acceptance 3).
    /// </summary>
    public IReadOnlyList<RunContext?> Scopes => _scopes;


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

    /// <summary>
    /// Queues a verdict the provider did not flag while a category's score sits above the caller's ceiling
    /// (02.8.2-T2 decision 5 (d)): the thresholds, not the provider's flag, decide.
    /// </summary>
    /// <param name="stage">The stage the verdict answers.</param>
    /// <param name="scores">The score per category.</param>
    /// <param name="flagged">Whether the provider flagged the text; <see langword="false"/> for the score-only case.</param>
    /// <param name="categories">The categories the provider flagged, if any.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeContentModerator EnqueueScores(
        ModerationStage stage,
        IReadOnlyDictionary<string, double> scores,
        bool flagged = false,
        params string[] categories) =>
        Enqueue(stage, () => new ModerationResult(flagged, categories, scores));

    /// <summary>
    /// Answers a flagged verdict for every check of <paramref name="stage"/> whose texts match
    /// <paramref name="matches"/> — the seam a batch call and the gate's own per-chunk attribution both need,
    /// because the second pass sends one text at a time and its order is the gate's business, not the test's.
    /// </summary>
    /// <remarks>
    /// A queued verdict of the same stage is answered first (the queue is the older seam), so a test may combine
    /// "the batch fails" with "only this chunk is the reason".
    /// </remarks>
    /// <param name="stage">The stage the verdict answers.</param>
    /// <param name="matches">Whether a check's texts are the ones the verdict is about (any text matching counts).</param>
    /// <param name="categories">The flagged categories.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeContentModerator EnqueueFlaggedWhen(
        ModerationStage stage,
        Func<IReadOnlyList<string>, bool> matches,
        params string[] categories)
    {
        ArgumentNullException.ThrowIfNull(matches);

        if (!_matchers.TryGetValue(stage, out var matchers))
        {
            _matchers[stage] = matchers = [];
        }

        matchers.Add((
            matches,
            () => new ModerationResult(true, categories, categories.ToDictionary(c => c, _ => 0.99, StringComparer.Ordinal))));

        return this;
    }

    /// <inheritdoc />
    public ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ct.ThrowIfCancellationRequested();

        _checks.Add((texts, stage));
        _scopes.Add(ModerationScope.Current);

        var verdict = _script.TryGetValue(stage, out var queue) && queue.Count > 0
            ? queue.Dequeue()()
            : Matching(stage, texts)?.Invoke() ?? ModerationResult.Clean;

        return ValueTask.FromResult(verdict);
    }

    // The first registered matcher of the stage that matches one of the check's texts, if any.
    private Func<ModerationResult>? Matching(ModerationStage stage, IReadOnlyList<string> texts)
    {
        if (!_matchers.TryGetValue(stage, out var matchers))
        {
            return null;
        }

        foreach (var (matches, verdict) in matchers)
        {
            foreach (var text in texts)
            {
                if (matches([text]))
                {
                    return verdict;
                }
            }
        }

        return null;
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
