using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.Infrastructure.Persistence.Outbox;

/// <summary>
/// The one serializer configuration of <c>outbox_messages.payload</c> (09 README D11, 09 plan §5): the
/// interceptor writes the payload with it and the dispatcher reads it back with it.
/// </summary>
/// <remarks>
/// <para>
/// Web defaults give the camelCase property names of the API contract; enums are written as their
/// snake_case wire name (never as an integer), so <c>npm_flagged</c> stays stable when a member is
/// renumbered. Only the concrete event type is serialized <em>and</em> deserialized — the payload carries
/// no discriminator, because <c>outbox_messages.type</c> is the discriminator and both sides resolve it
/// through <see cref="OutboxEventRegistry"/>.
/// </para>
/// <para>
/// A breaking payload change is a new event version (<c>story_approved.v2</c>), never an edit of the
/// stored shape (09 plan "Failure handling").
/// </para>
/// </remarks>
public static class OutboxJson
{
    /// <summary>The serializer configuration shared by the interceptor and the dispatcher.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };

        // No integers: a stored `1` would keep deserializing into whatever member holds that value today.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));

        return options;
    }
}
