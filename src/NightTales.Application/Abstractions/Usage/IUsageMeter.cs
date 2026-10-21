namespace NightTales.Application.Abstractions.Usage;

/// <summary>
/// The only writer of <c>usage_records</c> (02 README D14). One call writes one row <b>in its own
/// transaction</b>, independent of the job's: a job that later fails or is cancelled keeps its cost.
/// </summary>
public interface IUsageMeter
{
    /// <summary>Writes the row of one provider call.</summary>
    /// <param name="item">The call to record.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <exception cref="Domain.Usage.UnknownModelPricingException">The cost must be computed and the model is unpriced.</exception>
    Task RecordAsync(UsageItem item, CancellationToken ct);
}
