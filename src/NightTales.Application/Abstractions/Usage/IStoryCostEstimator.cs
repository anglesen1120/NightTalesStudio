using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Abstractions.Usage;

/// <summary>
/// The upper bound of one run's price (02 README D14, CR-6/T46): what the budget guard and the credit
/// ledger reserve before dispatch.
/// </summary>
public interface IStoryCostEstimator
{
    /// <summary>
    /// The worst case of the full pipeline: writer, review, revision and review #2 at their
    /// <c>max_output_tokens</c>, plus two zero-cost moderation calls.
    /// </summary>
    /// <param name="tier">The episode's length tier.</param>
    /// <param name="language">The content language.</param>
    /// <param name="ct">Cancels the estimate.</param>
    /// <exception cref="Domain.Usage.UnknownModelPricingException">A pipeline model is unpriced.</exception>
    Task<UsageBound> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct);
}
