using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The <c>Z</c> timestamp of 05.4.1-T2 §1 ("every timestamp is ISO-8601 UTC with <c>Z</c>"): a value
/// is read as an instant (any offset is accepted and normalised to UTC) and written as
/// <c>yyyy-MM-ddTHH:mm:ss[.fffffff]Z</c>, which is byte-identical to the D14 fixtures.
/// </summary>
public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string SecondFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private const string FractionFormat = "D7";

    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected an ISO-8601 UTC timestamp string, found {reader.TokenType}.");
        }

        var text = reader.GetString();
        if (!TryParse(text, out var value))
        {
            throw new JsonException("The timestamp is not an ISO-8601 instant.");
        }

        return value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));

    /// <summary>Parses an ISO-8601 instant and normalises it to UTC; never throws.</summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            value = parsed.ToUniversalTime();
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Formats an instant as the contract's <c>Z</c> timestamp (trailing zero fractions dropped).</summary>
    public static string Format(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime().UtcDateTime;
        var text = utc.ToString(SecondFormat, CultureInfo.InvariantCulture);
        var fraction = utc.Ticks % TimeSpan.TicksPerSecond;
        if (fraction != 0)
        {
            text += "." + fraction.ToString(FractionFormat, CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return text + "Z";
    }
}
