namespace NightTales.Application.Features.Catalog.Dtos;

/// <summary>
/// One row of <c>GET /api/v1/voices</c> (09.4.2 plan §5, 04 D9): the members the 10.4.x voice picker
/// needs, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Id"/> is the canonical VieNeu preset name — the <c>voices</c> primary key is a
/// <c>string</c> (09 W3 CR-41) — and is what a series stores as its <c>voiceId</c>.
/// </para>
/// <para>
/// <see cref="Language"/> and <see cref="Gender"/> are the snake_case wire values
/// (<c>vi</c>/<c>en</c>, <c>female</c>/<c>male</c>/<c>neutral</c>, 09 README D14) held as strings,
/// because these two catalogs are the only place where the wire spelling *is* the value a client
/// stores; keeping them as strings also makes the catalog ETag independent of the JSON enum
/// configuration.
/// </para>
/// <para>
/// <see cref="PreviewUrl"/> is <see langword="null"/> when the row has no preview key or the host has
/// no usable public bucket; the client then hides the play control instead of following a URL the
/// platform did not compose (plan §3, §8).
/// </para>
/// </remarks>
/// <param name="Id">The canonical voice id.</param>
/// <param name="DisplayName">The label the picker shows.</param>
/// <param name="Language">The voice language, snake_case.</param>
/// <param name="Gender">The voice gender, snake_case.</param>
/// <param name="Tags">The voice tags, in catalog order; empty when the voice has none.</param>
/// <param name="PreviewUrl">The public preview URL, or <see langword="null"/>.</param>
public sealed record VoiceListItemDto(
    string Id,
    string DisplayName,
    string Language,
    string Gender,
    IReadOnlyList<string> Tags,
    string? PreviewUrl);
