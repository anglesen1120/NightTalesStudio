namespace NightTales.Application.Abstractions.Jobs;

/// <summary>
/// One recurring job of the platform (09 README D12,D13): the Worker registers every implementation
/// found in DI at start, then deletes every stored recurring id that no implementation returned —
/// there is no prefix exception and no opt-out (09 W3 CR-42).
/// </summary>
/// <remarks>
/// Implementations live in <c>Worker/Jobs/</c>, are stateless, and are registered as singletons.
/// </remarks>
public interface IRecurringJobRegistration
{
    /// <summary>The stable recurring job id; it identifies the job across Worker starts.</summary>
    string Id { get; }

    /// <summary>Upserts this job through <paramref name="scheduler"/>, with a UTC cron expression.</summary>
    void Register(IJobScheduler scheduler);
}
