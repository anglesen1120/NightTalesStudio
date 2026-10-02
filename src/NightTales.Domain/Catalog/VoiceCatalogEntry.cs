namespace NightTales.Domain.Catalog;

/// <summary>
/// One entry of the tracked <c>seed/voices.v1.json</c> (04 README D9). The values stay raw strings —
/// the same file is read by the Python proxy (<c>nts_vieneu_proxy.catalog</c>) and by
/// <see cref="VoiceCatalogRules"/> — so an unknown <c>language</c> or <c>gender</c> is reported as a
/// rule failure instead of a parse exception on either side.
/// </summary>
/// <param name="Id">The canonical VieNeu preset name; the natural key of the <c>voices</c> row and of <c>series.voice_id</c>.</param>
/// <param name="Slug">The preview key's slug; <see cref="VoiceCatalogRules.Slugify"/> of the id.</param>
/// <param name="DisplayName">The shown name (1–40 characters after trimming).</param>
/// <param name="Language">The raw <c>vi</c>|<c>en</c>.</param>
/// <param name="Gender">The raw <c>female</c>|<c>male</c>|<c>neutral</c>.</param>
/// <param name="Tags">The tag keys, in file order (0–4 of the D9 closed list).</param>
/// <param name="Active">Whether the voice is offered by <c>GET /voices</c>.</param>
/// <param name="PreviewStorageKey">The object key in <c>nt-public-&lt;env&gt;</c>, or <c>null</c>.</param>
public sealed record VoiceCatalogEntry(
    string Id,
    string Slug,
    string DisplayName,
    string Language,
    string Gender,
    IReadOnlyList<string> Tags,
    bool Active,
    string? PreviewStorageKey);
