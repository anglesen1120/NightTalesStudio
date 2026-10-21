using Hangfire;
using Hangfire.Common;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// The platform's Hangfire filter chain and the one global mutation it needs (09 README D12).
/// </summary>
/// <remarks>
/// Hangfire's filter collections are process-global and are plain collections, so they must never be
/// mutated while jobs run: every host composes its own <see cref="IJobFilterProvider"/> from this
/// chain (the client and the server both resolve the DI registration), and the only global write —
/// removing Hangfire's 10-attempt default — happens once per process, under a gate, before the first
/// host starts.
/// </remarks>
public static class JobFilterPipeline
{
    private static readonly Lock Gate = new();
    private static bool _globalRetryDefaultRemoved;

    /// <summary>
    /// The filter chain of one host: <paramref name="extra"/> first (the harness's state observer),
    /// then the telemetry and attempt providers, the <see cref="JobPolicyAttribute"/> retry provider and
    /// Hangfire's own global filters.
    /// </summary>
    public static IJobFilterProvider Compose(params IJobFilterProvider[] extra) =>
        new JobFilterProviderCollection(
            [
                .. extra,
                new JobTelemetryFilterProvider(),
                new JobAttemptFilterProvider(),
                new JobPolicyFilterProvider(),
                JobFilterProviders.Providers,
            ]);

    /// <summary>
    /// Removes Hangfire's global <see cref="AutomaticRetryAttribute"/> (10 attempts) once per process,
    /// so a job without <see cref="JobPolicyAttribute"/> is attempted exactly once and only
    /// <see cref="JobPolicyFilterProvider"/> decides retries. Idempotent and gated.
    /// </summary>
    public static void RemoveGlobalAutomaticRetryDefault()
    {
        lock (Gate)
        {
            if (_globalRetryDefaultRemoved)
            {
                return;
            }

            GlobalJobFilters.Filters.Remove<AutomaticRetryAttribute>();
            _globalRetryDefaultRemoved = true;
        }
    }
}
