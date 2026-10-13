using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Facts;

/// <summary>
/// The <c>facts.v1</c> model output (02.5.3-T1): the facts the extractor read out of the fetched source
/// texts. Statements cite their sources by alias only; the model never emits a fact id or a URL (D6/F12,
/// CR-21). The strict schema is <c>prompts/schemas/facts.v1.json</c> and the deserialiser is the adapter's
/// snake_case options (<c>OpenAiTextClient</c>).
/// </summary>
/// <param name="Facts">The extracted facts; empty when the sources carry none.</param>
public sealed record FactsV1(IReadOnlyList<FactDraftV1> Facts);

/// <summary>One fact of a <c>facts.v1</c> as the model returns it (02.5.3-T1).</summary>
/// <param name="Statement">The fact, paraphrased in the run's language; never a copied source sentence (F18).</param>
/// <param name="SourceAliases">The aliases of the sources the fact is drawn from, <c>s1…s10</c>; at least one.</param>
/// <param name="Confidence">How well the cited sources support the statement (README T53).</param>
public sealed record FactDraftV1(
    string Statement,
    IReadOnlyList<string> SourceAliases,
    FactConfidence Confidence);
