namespace NightTales.Domain.Rendering.Music;

/// <summary>A parsed <c>seed/music-tracks.v1.json</c> (05 README D8): the revision and its tracks.</summary>
/// <param name="Revision">The file's <c>revision</c>; the seeder stores it as <c>catalog_revision</c>.</param>
/// <param name="Tracks">The entries, in file order.</param>
public sealed record MusicCatalogFile(int Revision, IReadOnlyList<MusicCatalogEntry> Tracks);

/// <summary>One entry of the catalog file: the display fields plus the rule-relevant <see cref="Track"/>.</summary>
/// <param name="Title">The track title.</param>
/// <param name="Artist">The artist.</param>
/// <param name="SourceUrl">Where the track was obtained.</param>
/// <param name="Track">The fields <see cref="MusicTrackRules"/> validates.</param>
public sealed record MusicCatalogEntry(string Title, string Artist, string SourceUrl, MusicTrackCandidate Track);
