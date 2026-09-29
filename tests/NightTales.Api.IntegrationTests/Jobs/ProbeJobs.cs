using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// What the probe jobs did (09.2.6-T1 tests): a run counter, the token Hangfire handed the job, and an
/// event-driven wait — so a test observes a job instead of sleeping and polling.
/// </summary>
public sealed class ProbeJobEffects
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runs;
    private CancellationToken _lastToken;
    private int _target = 1;
    private TaskCompletionSource? _reached;

    /// <summary>Completes when the test releases the blocking probe jobs.</summary>
    public Task Release => _release.Task;

    /// <summary>Releases every blocking probe job.</summary>
    public void AllowCompletion() => _release.TrySetResult();

    /// <summary>How many times a probe job ran.</summary>
    public int Runs
    {
        get
        {
            lock (_gate)
            {
                return _runs;
            }
        }
    }

    /// <summary>The cancellation token of the last run.</summary>
    public CancellationToken LastToken
    {
        get
        {
            lock (_gate)
            {
                return _lastToken;
            }
        }
    }

    /// <summary>Records one run.</summary>
    public void Ran(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _runs++;
            _lastToken = cancellationToken;

            if (_runs >= _target)
            {
                _reached?.TrySetResult();
            }
        }
    }

    /// <summary>Completes once <paramref name="count"/> runs happened, or throws after <paramref name="timeout"/>.</summary>
    public Task WaitForRunsAsync(int count, TimeSpan timeout)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_runs >= count)
            {
                return Task.CompletedTask;
            }

            if (_target != count || _reached is null)
            {
                _target = count;
                _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            signal = _reached;
        }

        return signal.Task.WaitAsync(timeout);
    }
}

/// <summary>A constructor dependency of the probe jobs, registered by <see cref="ProbeHost"/>.</summary>
public sealed class ProbeJobDependency
{
    /// <summary>Read by <see cref="DependencyProbeJob"/>, which proves the DI scope resolved it.</summary>
    public Guid Id { get; } = Guid.CreateVersion7();
}

/// <summary>Records one run and touches its constructor dependency (no database access).</summary>
public sealed class RecordingProbeJob(ProbeJobEffects effects) : IBackgroundJob
{
    /// <inheritdoc />
    public Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        effects.Ran(cancellationToken);

        return Task.CompletedTask;
    }
}

/// <summary>Records one run and writes one <c>probe_job_effects</c> row (the Npgsql 10 spike).</summary>
public sealed class EffectRowProbeJob(ProbeJobEffects effects, IAppDbContext database) : IBackgroundJob
{
    /// <inheritdoc />
    public async Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlRawAsync("INSERT INTO probe_job_effects (job_id) VALUES ({0})", id);
        effects.Ran(cancellationToken);
    }
}

/// <summary>
/// Runs on the <c>render</c> queue because of its <see cref="JobPolicyAttribute"/> and blocks until
/// the test releases it, so the queue-routing test can inspect the job while it is being processed.
/// </summary>
[JobPolicy(Queue = JobQueue.Render)]
public sealed class RenderQueueProbeJob(ProbeJobEffects effects) : IBackgroundJob
{
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        effects.Ran(cancellationToken);

        await effects.Release.WaitAsync(ReleaseTimeout, cancellationToken);
    }
}

/// <summary>Always throws, with the platform default of 0 retries (<see cref="JobPolicyAttribute"/> absent).</summary>
public sealed class DefaultFailingProbeJob(ProbeJobEffects effects) : IBackgroundJob
{
    /// <summary>The exception message every failing probe job throws.</summary>
    public const string FailureMessage = "probe job failure (09.2.6-T1)";

    /// <inheritdoc />
    public Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        effects.Ran(cancellationToken);

        throw new InvalidOperationException(FailureMessage);
    }
}

/// <summary>Always throws with <c>[JobPolicy(Retries = 0)]</c>: it must not be retried.</summary>
[JobPolicy(Retries = 0)]
public sealed class FailingProbeJob(ProbeJobEffects effects) : IBackgroundJob
{
    /// <inheritdoc />
    public Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        effects.Ran(cancellationToken);

        throw new InvalidOperationException(DefaultFailingProbeJob.FailureMessage);
    }
}

/// <summary>Always throws with <c>[JobPolicy(Retries = 2)]</c>: it is attempted three times.</summary>
[JobPolicy(Retries = 2)]
public sealed class RetryingProbeJob(ProbeJobEffects effects) : IBackgroundJob
{
    /// <inheritdoc />
    public Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        effects.Ran(cancellationToken);

        throw new InvalidOperationException(DefaultFailingProbeJob.FailureMessage);
    }
}

/// <summary>
/// Depends on <see cref="ProbeJobDependency"/>; the activation-check test removes that registration,
/// which is the only way a scanned job becomes unresolvable.
/// </summary>
public sealed class DependencyProbeJob(ProbeJobEffects effects, ProbeJobDependency dependency) : IBackgroundJob
{
    /// <inheritdoc />
    public Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        effects.Ran(cancellationToken);

        // Touching the dependency proves the DI scope resolved the constructor graph.
        _ = dependency.Id;

        return Task.CompletedTask;
    }
}
