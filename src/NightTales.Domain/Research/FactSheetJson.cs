using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.Domain.Research;

/// <summary>
/// The single canonical JSON shape of a fact sheet's <c>facts</c> and <c>source_aliases</c> documents
/// (02.5.3 plan §3). <see cref="FactSheet.ComputeContentSha256"/> hashes exactly the bytes
/// <see cref="SerializeFacts"/> writes, and the EF value converters persist the same bytes into the
/// <c>jsonb</c> columns, so the stored document and the hash can never drift.
/// </summary>
public static class FactSheetJson
{
    /// <summary>
    /// The canonical serialiser settings: snake_case property names, no indentation, every property written
    /// (a null overlap is an explicit <c>null</c>), invariant formatting for <see cref="Guid"/> and the enum
    /// wire names — so the same facts always produce the same bytes on every machine.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serialises the fact list in its declaration order (the string the content hash is taken over).</summary>
    /// <param name="facts">The facts, in output order.</param>
    /// <returns>The canonical JSON array.</returns>
    public static string SerializeFacts(IReadOnlyList<FactItem> facts) =>
        JsonSerializer.Serialize(facts, Options);

    /// <summary>Serialises the alias→source-id map for the <c>source_aliases jsonb</c> column (CR-4/D21).</summary>
    /// <param name="sourceAliases">The alias map written on every version.</param>
    /// <returns>The canonical JSON object.</returns>
    public static string SerializeAliases(IReadOnlyDictionary<string, Guid> sourceAliases) =>
        JsonSerializer.Serialize(sourceAliases, Options);
}
