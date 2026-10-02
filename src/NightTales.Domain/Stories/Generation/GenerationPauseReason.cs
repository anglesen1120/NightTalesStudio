namespace NightTales.Domain.Stories.Generation;

/// <summary>
/// Why a queued <see cref="GenerationRun"/> is held (02 TL T19, 12.8.3), stored as snake_case text in
/// <c>generation_runs.pause_reason</c>: our own USD cap, or the provider account cannot be charged.
/// </summary>
public enum GenerationPauseReason
{
    SpendCapReached,
    ProviderFundsExhausted,
}
