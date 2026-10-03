namespace NightTales.Domain.Usage;

/// <summary>
/// Thrown when a model has no entry in the active <c>price_versions.rates</c> document
/// (02 README D14): the cost cannot be computed, so dispatch must stop before a paid call runs
/// unpriced. It is a configuration error, not a provider failure — never retried.
/// </summary>
public sealed class UnknownModelPricingException : InvalidOperationException
{
    /// <summary>Creates the exception for the unpriced <paramref name="model"/>.</summary>
    public UnknownModelPricingException(string model)
        : base($"No price for model '{model}' in the active price_versions row: update the OpenAi:Pricing seed or add the model before dispatching the step.")
    {
        Model = model;
    }

    /// <summary>The model name that had no rate entry.</summary>
    public string Model { get; }
}
