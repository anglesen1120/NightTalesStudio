namespace NightTales.Domain.Rendering.Music;

/// <summary>A parsed <c>seed/music-tracks.v1.json</c> (05 README D8): the revision and its tracks.</summary>
/// <param name="Revision">The file's <c>revision</c>; the seeder stores it as <c>catalog_revision</c>.</param>
/// <param name="Tracks">The entries, in file order; never empty.</param>
public sealed record MusicCatalogFile(int Revision, IReadOnlyList<MusicTrackCandidate> Tracks);
