using System.Text.Json.Serialization;

namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// The ids a real-time event is about (09 README BD11/BD16: an ids-only payload; 09.8.1 plan §5 marks
/// the member list [INFERENCE]).
/// </summary>
/// <remarks>
/// A member that does not apply to the event is <c>null</c> and is left out of the JSON, so a client
/// always sees exactly the ids the event carries. Never a title, a story text, an e-mail address or a
/// storage key: the NOTIFY body is read by every Api instance and forwarded to a browser.
/// </remarks>
public sealed record RealtimeIds(
    [property: JsonPropertyName("seriesId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? SeriesId = null,
    [property: JsonPropertyName("episodeId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? EpisodeId = null,
    [property: JsonPropertyName("renderJobId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? RenderJobId = null,
    [property: JsonPropertyName("publicationId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? PublicationId = null,
    [property: JsonPropertyName("storyVersionId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? StoryVersionId = null);
