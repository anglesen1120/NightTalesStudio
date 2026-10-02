namespace NightTales.Domain.Usage;

/// <summary>
/// The exact USD cost of one text call from the rates that were active at reserve time
/// (02 README D14): pure, no I/O, <c>decimal</c> end to end so a cost never loses a fraction of a
/// cent to a float.
/// </summary>
/// <remarks>
/// <code>
/// cost = (input − cached) × r_in + cached × r_cached + cache_write × r_write + output × r_out   // per 1M tokens
/// </code>
/// <para>
/// Reasoning tokens are part of <c>output</c> (O4: <c>max_output_tokens</c> covers both), never an
/// extra term. When <c>input &gt; surchargeThresholdTokens</c> the O3 long-context rule applies to the
/// whole request: the input and cache-write rates double and the output rate grows by 1.5.
/// </para>
/// </remarks>
public static class TextCostCalculator
{
    /// <summary>
    /// The O3 default of <c>OpenAi:Pricing:SurchargeThresholdTokens</c> (&gt; 272K input tokens cost
    /// 2× input and 1.5× output). The live value comes from the price version's rates document, so a
    /// re-priced threshold versions with the rates.
    /// </summary>
    public const int DefaultSurchargeThresholdTokens = 272_000;

    /// <summary>
    /// Computes the cost, or <c>null</c> when the provider returned no usage: the caller then writes
    /// <c>estimated = true</c> and prices the row at the D1 bound instead (never <c>0</c>).
    /// </summary>
    /// <param name="rates">The model's rates of the run's price version.</param>
    /// <param name="inputTokens">Reported input tokens; <c>null</c> means the response carried no usage.</param>
    /// <param name="cachedInputTokens">Cache-hit input tokens; <c>null</c> counts as none.</param>
    /// <param name="cacheWriteTokens">Cache-write tokens; <c>null</c> counts as none.</param>
    /// <param name="outputTokens">Output tokens including reasoning; <c>null</c> counts as none.</param>
    /// <param name="surchargeThresholdTokens">The version's long-context threshold.</param>
    /// <returns>The USD cost, rounded to the <c>numeric(12,6)</c> scale of <c>usage_records.cost_usd</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cachedInputTokens"/> exceeds <paramref name="inputTokens"/> — a provider bug must
    /// surface as an error, never as a negative cost.
    /// </exception>
    public static decimal? Compute(
        PriceRates rates,
        long? inputTokens,
        long? cachedInputTokens,
        long? cacheWriteTokens,
        long? outputTokens,
        int surchargeThresholdTokens = DefaultSurchargeThresholdTokens)
    {
        ArgumentNullException.ThrowIfNull(rates);

        if (inputTokens is null)
        {
            return null;
        }

        var input = inputTokens.Value;
        var cached = cachedInputTokens ?? 0;
        var cacheWrite = cacheWriteTokens ?? 0;
        var output = outputTokens ?? 0;

        ArgumentOutOfRangeException.ThrowIfNegative(input);
        ArgumentOutOfRangeException.ThrowIfNegative(cached);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheWrite);
        ArgumentOutOfRangeException.ThrowIfNegative(output);

        if (cached > input)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cachedInputTokens),
                cached,
                $"cached_input_tokens ({cached}) cannot exceed input_tokens ({input}): a provider miscount would produce a negative cost.");
        }

        var inputRate = rates.Input;
        var cacheWriteRate = rates.CacheWrite;
        var outputRate = rates.Output;

        // O3: "> 272K input tokens" — strictly above; at exactly the threshold there is no surcharge.
        if (input > surchargeThresholdTokens)
        {
            inputRate *= 2m;
            cacheWriteRate *= 2m;
            outputRate *= 1.5m;
        }

        const decimal PerMillion = 1_000_000m;
        var cost =
            ((input - cached) * inputRate
             + cached * rates.CachedInput
             + cacheWrite * cacheWriteRate
             + output * outputRate)
            / PerMillion;

        return Math.Round(cost, 6, MidpointRounding.AwayFromZero);
    }
}
