namespace NightTales.Domain.Catalog;

/// <summary>
/// The whole tracked <c>seed/voices.v1.json</c> (04 README D9): the file-level identity of the catalog
/// the proxy and the backend both read. <see cref="Schema"/>, <see cref="Revision"/> and
/// <see cref="VieneuCommit"/> are never stored in the <c>voices</c> table — they identify the file
/// revision the rows came from.
/// </summary>
/// <param name="Schema">The document schema; exactly <see cref="VoiceCatalogRules.Schema"/>.</param>
/// <param name="Revision">
/// The catalog revision; <c>0</c> is Development-only, <c>1</c>+ is the curated catalog. It is a
/// <see cref="long"/> because the normative proxy reader accepts any JSON integer (Python has no
/// <c>Int32</c> bound), so the C# side does not invent one below <see cref="long"/>.
/// </param>
/// <param name="VieneuCommit">The 40-hex commit the ids were checked against.</param>
/// <param name="Voices">The entries, in file order.</param>
public sealed record VoiceCatalogFile(
    string Schema,
    long Revision,
    string VieneuCommit,
    IReadOnlyList<VoiceCatalogEntry> Voices);
