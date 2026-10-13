using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>
/// The support a fact's statement has in its cited sources (02 README T53). The wire spelling is the
/// snake_case of the member name — <c>high</c>, <c>medium</c>, <c>low</c> — which is what
/// <c>facts.v1</c> restricts the value to. <see cref="Low"/> is an advisory UI hint only: the validator
/// (02.5.3-T2) never drops a fact for it.
/// </summary>
[JsonConverter(typeof(FactConfidenceJsonConverter))]
public enum FactConfidence
{
    /// <summary>The sources state the fact directly.</summary>
    High = 0,

    /// <summary>The sources support the fact, but not verbatim.</summary>
    Medium = 1,

    /// <summary>The sources only hint at the fact; an advisory hint, never a drop (T53).</summary>
    Low = 2,
}

/// <summary>
/// Reads and writes <see cref="FactConfidence"/> as its snake_case wire name, so the strict <c>facts.v1</c>
/// enum (<c>high|medium|low</c>) round-trips. A value outside the closed list, or a non-string token, is a
/// <see cref="JsonException"/> the adapter maps to <c>InvalidOutput</c> (D7) instead of a silently wrong fact.
/// </summary>
internal sealed class FactConfidenceJsonConverter : JsonConverter<FactConfidence>
{
    /// <inheritdoc />
    public override FactConfidence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A fact confidence must be a string.");
        }

        return reader.GetString() switch
        {
            "high" => FactConfidence.High,
            "medium" => FactConfidence.Medium,
            "low" => FactConfidence.Low,
            var other => throw new JsonException($"Unknown fact confidence '{other}'."),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FactConfidence value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value switch
        {
            FactConfidence.High => "high",
            FactConfidence.Medium => "medium",
            FactConfidence.Low => "low",
            _ => throw new JsonException("Unknown fact confidence."),
        });
    }
}
