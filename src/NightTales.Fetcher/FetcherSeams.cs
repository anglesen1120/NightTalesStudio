using ErrorOr;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Fetcher.InternalApi;

namespace NightTales.Fetcher;

/// <summary>How the loop asks the process to end with a non-zero code (the supervisor then restarts it, T43). Tests observe it.</summary>
public interface IFetcherExit
{
    /// <summary>Requests a stop of the host with <paramref name="exitCode"/>; safe to call more than once.</summary>
    /// <param name="exitCode">The process exit code (1).</param>
    /// <param name="reason">A short, secret-free reason for the log.</param>
    void RequestExit(int exitCode, string reason);
}

/// <summary>The production seam: sets <see cref="Environment.ExitCode"/> and stops the host gracefully.</summary>
/// <param name="lifetime">The host lifetime.</param>
public sealed class HostFetcherExit(IHostApplicationLifetime lifetime) : IFetcherExit
{
    /// <inheritdoc />
    public void RequestExit(int exitCode, string reason)
    {
        Environment.ExitCode = exitCode;
        lifetime.StopApplication();
    }
}

/// <summary>
/// Runs a <c>find_sources</c> task (02.7.2-T3 owns the implementation; this host only routes). No implementation is
/// registered before that task lands: the loop then logs and leaves the lease to expire.
/// </summary>
public interface ISourceFinderRunner
{
    /// <summary>Finds candidate sources for <paramref name="task"/>.</summary>
    /// <param name="task">The leased task with its <see cref="LeasedTask.FinderRequest"/>.</param>
    /// <param name="ct">Cancelled on shutdown or a lost lease.</param>
    ValueTask<ErrorOr<IReadOnlyList<ResultCandidate>>> FindAsync(LeasedTask task, CancellationToken ct);
}

/// <summary>The loop's own keys, bound from the <c>Fetcher</c> section (02.5.2-T3 Design).</summary>
public sealed class FetcherLoopOptions
{
    /// <summary>How long the loop waits after a lease that returned nothing, in milliseconds (2 s).</summary>
    public int IdlePollMilliseconds { get; set; } = 2_000;

    /// <summary>How long the loop waits when every lease slot is busy, in milliseconds (500 ms).</summary>
    public int BusyPollMilliseconds { get; set; } = 500;

    /// <summary>The consecutive failed lease calls after which the host exits non-zero (T43: 5).</summary>
    public int ConsecutiveFailuresBeforeExit { get; set; } = 5;
}

/// <summary>Fails the start-up on a non-positive loop key.</summary>
public sealed class FetcherLoopOptionsValidator : IValidateOptions<FetcherLoopOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FetcherLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        if (options.IdlePollMilliseconds < 1)
        {
            problems.Add("'Fetcher:IdlePollMilliseconds' must be at least 1.");
        }

        if (options.BusyPollMilliseconds < 1)
        {
            problems.Add("'Fetcher:BusyPollMilliseconds' must be at least 1.");
        }

        if (options.ConsecutiveFailuresBeforeExit < 1)
        {
            problems.Add("'Fetcher:ConsecutiveFailuresBeforeExit' must be at least 1.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
