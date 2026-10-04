using ErrorOr;
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
    /// <returns>
    /// The bound, or a typed error the caller fails closed on: <c>price_catalog_empty</c> (no active price
    /// version) or <c>prompt_not_shipped</c> (a pipeline step has no shipped prompt yet).
    /// </returns>
    /// <exception cref="Domain.Usage.UnknownModelPricingException">A pipeline model is unpriced.</exception>
    Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct);

    /// <summary>
    /// The worst case of one writer call alone, at its <c>max_output_tokens</c>: the bound of a metered writer
    /// call whose response carried no usage or whose usage cannot be priced (02.2.1-T2). It measures only the
    /// writer prompt, so unlike <see cref="WorstCaseAsync"/> it does not depend on the review and revision prompts shipping.
    /// </summary>
    /// <param name="tier">The episode's length tier.</param>
    /// <param name="language">The content language.</param>
    /// <param name="ct">Cancels the estimate.</param>
    /// <returns>The bound, or <c>price_catalog_empty</c> / <c>prompt_not_shipped</c> (the writer prompt itself).</returns>
    /// <exception cref="Domain.Usage.UnknownModelPricingException">The writer model is unpriced.</exception>
    Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct);

    /// <summary>
    /// The worst case of one review call alone, at its <c>max_output_tokens</c>: the bound of a metered review call
    /// whose response carried no usage or whose usage cannot be priced (02.6.1-T1). It measures only the review
    /// prompt and the story it carries, so it does not depend on the revision prompt shipping.
    /// </summary>
    /// <param name="tier">The episode's length tier.</param>
    /// <param name="language">The content language.</param>
    /// <param name="ct">Cancels the estimate.</param>
    /// <returns>The bound, or <c>price_catalog_empty</c> / <c>prompt_not_shipped</c> (the review prompt itself).</returns>
    /// <exception cref="Domain.Usage.UnknownModelPricingException">The review model is unpriced.</exception>
    Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct);
}
