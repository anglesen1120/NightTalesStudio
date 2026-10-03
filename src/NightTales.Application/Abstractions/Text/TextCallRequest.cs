namespace NightTales.Application.Abstractions.Text;

/// <summary>One structured-output text call (02 README D21): two strings, a strict schema and the call's bounds.</summary>
/// <param name="Run">The run the call belongs to; <c>Attempt</c> is the run's attempt, not an HTTP retry.</param>
/// <param name="Step">The step; selects the model, effort and timeout.</param>
/// <param name="SystemPrompt">The static system prompt (first in the cache prefix, O9).</param>
/// <param name="UserDataJson">The one JSON data object (last, so the prefix stays cacheable).</param>
/// <param name="SchemaName">The schema's logical name, for example <c>story.v1</c>.</param>
/// <param name="Schema">The strict JSON schema bytes the output must match (the catalog's bytes; the adapter wraps them in the SDK type).</param>
/// <param name="MaxOutputTokens">The call's <c>max_output_tokens</c>, reasoning included (O4).</param>
/// <param name="PromptCacheKey">The provider prompt-cache key.</param>
/// <param name="PromptVersion">The composite prompt version, for logs only.</param>
public sealed record TextCallRequest(
    RunContext Run,
    TextStep Step,
    string SystemPrompt,
    string UserDataJson,
    string SchemaName,
    ReadOnlyMemory<byte> Schema,
    int MaxOutputTokens,
    string PromptCacheKey,
    string PromptVersion)
{
    /// <summary>
    /// The model of the request once it may have reached the provider; <see langword="null"/> while nothing was
    /// sent (02.2.1-T2 SEC-2). The client sets it just before the first attempt, so a caller that sees its call
    /// cancelled knows whether a billed generation is plausible.
    /// </summary>
    public string? DispatchedModel { get; private set; }

    /// <summary>Records that the request may now be in flight (or already billed) on <paramref name="model"/>.</summary>
    /// <param name="model">The model the call is sent to.</param>
    public void MarkDispatched(string model) => DispatchedModel = model;
}
