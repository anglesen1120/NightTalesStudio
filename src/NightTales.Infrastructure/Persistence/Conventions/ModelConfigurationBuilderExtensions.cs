using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// Registers the Night Tales model conventions. Both contexts call it from their
/// <c>ConfigureConventions</c> override, so the write and the read model stay identical
/// (09 README D8, C1).
/// </summary>
public static class ModelConfigurationBuilderExtensions
{
    public static ModelConfigurationBuilder ApplyNightTalesConventions(this ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Every DateTimeOffset is a timestamptz (09 README D8, C7).
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");

        // 02.5.3-T2: the two fact-sheet documents are jsonb scalars. Registering the conversions here —
        // before the conventions run — is what makes EF discover `FactSheet.Facts` and
        // `FactSheet.SourceAliases` as converted properties instead of collection navigations (they hold
        // `FactItem` records, which are not entities). FactSheetConfiguration then fixes only the column
        // type, and the bytes are the canonical documents of FactSheetJson (so the stored document and
        // `content_sha256` cannot drift).
        configurationBuilder.Properties<IReadOnlyList<FactItem>>().HaveConversion<FactSheetFactsJsonConverter>();
        configurationBuilder.Properties<IReadOnlyDictionary<string, Guid>>().HaveConversion<FactSheetAliasesJsonConverter>();

        configurationBuilder.Conventions.Add(_ => new EntityIdConvention());
        configurationBuilder.Conventions.Add(_ => new AggregateRootConvention());
        configurationBuilder.Conventions.Add(_ => new EnumCheckConvention());
        configurationBuilder.Conventions.Add(_ => new AuditTimestampConvention());

        return configurationBuilder;
    }
}

/// <summary>
/// Writes and reads <c>fact_sheets.facts</c> as the canonical JSON array of
/// <see cref="FactSheetJson.SerializeFacts"/> (02.5.3-T2), the exact string the content hash is taken over.
/// </summary>
public sealed class FactSheetFactsJsonConverter : ValueConverter<IReadOnlyList<FactItem>, string>
{
    /// <summary>Creates the converter with the canonical <see cref="FactSheetJson"/> settings.</summary>
    public FactSheetFactsJsonConverter()
        : base(
            facts => FactSheetJson.SerializeFacts(facts),
            json => ParseFacts(json))
    {
    }

    private static IReadOnlyList<FactItem> ParseFacts(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<FactItem>>(json, FactSheetJson.Options)
        ?? throw new InvalidOperationException("A persisted facts document is never null: the column is NOT NULL.");
}

/// <summary>
/// Writes and reads <c>fact_sheets.source_aliases</c> as the canonical JSON object of
/// <see cref="FactSheetJson.SerializeAliases"/> (02.5.3-T2, CR-4/D21).
/// </summary>
public sealed class FactSheetAliasesJsonConverter : ValueConverter<IReadOnlyDictionary<string, Guid>, string>
{
    /// <summary>Creates the converter with the canonical <see cref="FactSheetJson"/> settings.</summary>
    public FactSheetAliasesJsonConverter()
        : base(
            aliases => FactSheetJson.SerializeAliases(aliases),
            json => ParseAliases(json))
    {
    }

    private static Dictionary<string, Guid> ParseAliases(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, Guid>>(json, FactSheetJson.Options)
        ?? throw new InvalidOperationException("A persisted alias document is never null: the column is NOT NULL.");
}
