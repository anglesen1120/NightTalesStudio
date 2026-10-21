namespace NightTales.Application.Abstractions.Text;

/// <summary>The token counts a provider reported for one call (02 README O4).</summary>
/// <param name="InputTokens">Input tokens, cached ones included.</param>
/// <param name="CachedInputTokens">Input tokens served from the prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to the prompt cache.</param>
/// <param name="OutputTokens">Output tokens, reasoning tokens included (O4).</param>
/// <param name="ReasoningTokens">The reasoning share of <paramref name="OutputTokens"/>.</param>
public sealed record TokenUsage(
    int InputTokens,
    int CachedInputTokens,
    int CacheWriteTokens,
    int OutputTokens,
    int ReasoningTokens);
