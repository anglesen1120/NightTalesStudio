using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Stories.Writing;

/// <summary>The catalog coordinates of one writer call.</summary>
/// <param name="Topic">The narrative topic, which selects the preset.</param>
/// <param name="Language">The content language, which selects the preset and the language-specific blocks.</param>
/// <param name="PresetKey">The preset's manifest key, for example <c>story/ghost.vi.v1.md</c>.</param>
/// <param name="WriterMaxOutputTokens">The <c>max_output_tokens</c> of the writing call, carried for the caller's request.</param>
public sealed record AssemblerContext(Topic Topic, Language Language, string PresetKey, int WriterMaxOutputTokens);
