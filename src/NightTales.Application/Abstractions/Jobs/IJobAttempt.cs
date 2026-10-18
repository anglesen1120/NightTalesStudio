namespace NightTales.Application.Abstractions.Jobs;

/// <summary>
/// Which processing attempt of a background job is running right now, and how many its
/// <see cref="JobPolicyAttribute"/> allows (09.6.3-T2 step 5, WT52).
/// </summary>
/// <remarks>
/// <para>
/// A job that a scheduler may hand back needs to tell a retryable failure from its last chance: 09.6.3-T2's
/// ingest throws on an output that does not verify so the scheduler runs it again, but on the last allowed
/// attempt it must end the <c>render_jobs</c> row itself — letting the scheduler run out of attempts leaves the
/// row in <c>ingesting</c> with no claim left, which is exactly the stranding WT52 rules out ([RULED — README
/// §B10 WT52/WT28]).
/// </para>
/// <para>
/// The attempt number belongs to the scheduler, and the Application layer may not reference one (D1/D12): the
/// Infrastructure filter publishes it through <see cref="NightTales.Application.Abstractions.Jobs.JobAttemptScope"/>,
/// and the registered implementation (<see cref="AmbientJobAttempt"/>) reads it. A job therefore depends on this
/// port alone and stays testable with a scripted implementation.
/// </para>
/// <para>
/// <b>Outside a scheduling pipeline the answer is "one attempt, and it is the last".</b> Nothing publishes the
/// scope when a job is called directly (a test host, a CLI verb), so treating that run as the final one keeps the
/// job self-ending instead of throwing into a caller that has no retry to give it — the same fail-safe direction
/// as the rest of the Worker's terminal handling.
/// </para>
/// </remarks>
public interface IJobAttempt
{
    /// <summary>The processing attempt now running, counted from 1.</summary>
    int Attempt { get; }

    /// <summary>How many attempts this job class is allowed in total (1 when it declares no retries).</summary>
    int MaxAttempts { get; }

    /// <summary>Whether <see cref="Attempt"/> is the last allowed one, so a retryable failure must end the row.</summary>
    bool IsLastAttempt => Attempt >= MaxAttempts;
}
