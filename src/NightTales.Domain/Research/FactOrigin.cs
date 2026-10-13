using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.Domain.Research;

/// <summary>
/// Who produced one <see cref="FactItem"/> statement (02.5.3 plan §3): the extraction model, or a person
/// through the fact-sheet editor. The persisted/wire spelling is the snake_case of the member name, so the
/// stored <c>facts</c> document and the API DTO read the same values.
/// </summary>
[JsonConverter(typeof(FactOriginJsonConverter))]
public enum FactOrigin
{
    /// <summary>The statement came out of the fact-extraction model.</summary>
    Model = 0,

    /// <summary>The statement was written or edited by a user through the fact-sheet editor.</summary>
    User = 1,
}

/// <summary>
/// Reads and writes <see cref="FactOrigin"/> as its snake_case wire name (<c>model|user</c>), so the canonical
/// JSON of a fact sheet is stable and the DTO spelling matches the stored document. A value outside the closed
/// list, or a non-string token, is a <see cref="System.Text.Json.JsonException"/>.
/// </summary>
public sealed class FactOriginJsonConverter : JsonConverter<FactOrigin>
{
    /// <inheritdoc />
    public override FactOrigin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A fact origin must be a string.");
        }

        return reader.GetString() switch
        {
            "model" => FactOrigin.Model,
            "user" => FactOrigin.User,
            var other => throw new JsonException($"Unknown fact origin '{other}'."),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FactOrigin value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value switch
        {
            FactOrigin.Model => "model",
            FactOrigin.User => "user",
            _ => throw new JsonException("Unknown fact origin."),
        });
    }
}
