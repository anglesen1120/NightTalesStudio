using System.Text.Json.Serialization;
using NightTales.Domain.Series.Bible;

namespace NightTales.Infrastructure.Bible;

/// <summary>
/// The source-generated <c>System.Text.Json</c> metadata of the two <c>series_bibles</c> documents (02.6.3 plan
/// §3.3): the <c>content</c> document (<see cref="SeriesBibleContent"/>, <c>bible.v1</c>) and the
/// <c>proposed_content</c> document (<see cref="BibleProposal"/>).
/// </summary>
/// <remarks>
/// <para>
/// Snake_case property names, no indentation, every property written, and the default (non-relaxed) encoder: the
/// stored bytes are the canonical document, and <c>BibleJsonContext.Default</c> is what
/// <see cref="Persistence.Configurations.SeriesBibleConfiguration"/>'s two converters use.
/// </para>
/// <para>
/// The Domain's <see cref="BibleJson"/> holds the same settings for the serializer it measures the
/// <c>MaxContentBytes</c> limit with (<c>BibleContentValidator.Utf8ByteCount</c>), because the Domain cannot see
/// this context. The two must produce identical bytes — otherwise the enforced limit and the stored document
/// would drift — and <c>BibleJsonContextTests</c> (NightTales.Infrastructure.UnitTests) pins that equality.
/// </para>
/// <para>
/// Public because this solution has no <c>InternalsVisibleTo</c> (the same reason <c>PromptResourceSource</c> is
/// public): its unit test must be able to serialise through it.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(SeriesBibleContent))]
[JsonSerializable(typeof(BibleProposal))]
public sealed partial class BibleJsonContext : JsonSerializerContext
{
}
