namespace NightTales.Application.Features.Stories.Writing;

/// <summary>The two strings of one writer call plus the values that identify and cache them (02 README D5/D6/D7).</summary>
/// <param name="SystemPrompt">The static system prompt: writer system, preset and shared rules.</param>
/// <param name="UserDataJson">The one JSON data object carrying every piece of untrusted material.</param>
/// <param name="PromptVersion">The composite <c>prompt_version</c> of the prompt set used.</param>
/// <param name="PromptCacheKey">The provider prompt-cache key, <c>&lt;step&gt;:&lt;preset&gt;</c>.</param>
public sealed record AssembledPrompt(string SystemPrompt, string UserDataJson, string PromptVersion, string PromptCacheKey);
