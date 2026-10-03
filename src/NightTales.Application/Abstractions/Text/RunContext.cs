using NightTales.Application.Abstractions.Money;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Abstractions.Text;

/// <summary>The run-level facts that travel with every text call of a generation run (02 README D21).</summary>
/// <param name="GenerationRunId">The run's id.</param>
/// <param name="UserId">The owner of the run.</param>
/// <param name="SeriesId">The series the run belongs to.</param>
/// <param name="EpisodeId">The episode; <see langword="null"/> only for series-level runs.</param>
/// <param name="Reservation">The credit reservation the run settles against.</param>
/// <param name="Trigger">What started the run.</param>
/// <param name="Attempt">The attempt number: 1 for the first call, 2 for the single invalid-output retry.</param>
/// <param name="PriceVersion">The price version the run is costed with, as text.</param>
public sealed record RunContext(
    Guid GenerationRunId,
    Guid UserId,
    Guid SeriesId,
    Guid? EpisodeId,
    ReservationId Reservation,
    GenerationTrigger Trigger,
    int Attempt,
    string PriceVersion);
