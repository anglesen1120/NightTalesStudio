using System.Text.Json;

namespace NightTales.Application.Features.Email;

/// <summary>
/// The one place the render values of an <c>email_messages</c> row are turned into JSON and back
/// (09.2.6-T3).
/// </summary>
/// <remarks>
/// The column is <c>jsonb</c> and the value is a flat string-to-string object, so a plain
/// <see cref="JsonSerializer"/> call is the whole contract: the same text the queue wrote is what the job
/// reads on a retry days later, when the outbox row that carried the event is long purged.
/// </remarks>
internal static class EmailParamsJson
{
    /// <summary>Serializes the allow-listed values of one message.</summary>
    /// <param name="values">The producer's values; empty is legal and becomes <c>{}</c>.</param>
    internal static string ToJson(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return JsonSerializer.Serialize(values);
    }

    /// <summary>Reads back the values of one stored row.</summary>
    /// <param name="json">The <c>params</c> value; never null at the call sites (a sent row has none).</param>
    internal static Dictionary<string, string> FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException("An `email_messages.params` value must be a JSON object of strings.");
    }
}
