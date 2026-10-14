using System.Globalization;
using ErrorOr;
using NightTales.Application.Common;
using NightTales.Domain.Series.Bible;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// The bible's ETag (02 README D9, 02.6.3-T1): <c>"sb-&lt;series_bibles.version&gt;"</c>, and the
/// <c>If-Match</c> answer of <c>PUT /api/v1/series/{id}/bible</c>, whose header is required.
/// </summary>
/// <remarks>
/// <para>
/// The comparison itself is the shared one (<see cref="ETags.Matches"/> — strong, exact and quoted; a weak
/// tag, a comma list and <c>*</c> are all refused, because a save must name the tag the client read); this
/// type only names the bible's value and splits the two failures of a required header into the errors the
/// route answers, the way the story route's <c>StoryEtag</c> does. It is public because the unit tests of
/// this feature exercise it and this solution has no <c>InternalsVisibleTo</c>.
/// </para>
/// <para>
/// The value is the strong ETag with its quotes, exactly as the <c>ETag</c> header and the DTO's
/// <c>etag</c> member carry it, so the client echoes it back unchanged.
/// </para>
/// </remarks>
public static class BibleEtags
{
    /// <summary>The bible ETag of one version: <c>"sb-&lt;version&gt;"</c>, quotes included.</summary>
    /// <param name="version">The <c>series_bibles.version</c> the tag names; never an <c>xmin</c> (D9).</param>
    /// <returns>The strong ETag.</returns>
    public static string For(int version) => $"\"sb-{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Whether an <c>If-Match</c> header names the version of <paramref name="version"/>: the exact quoted
    /// tag, through <see cref="ETags.Matches"/>. What is not judged here is the absence of the header — a
    /// command answers that with 428 before it asks.
    /// </summary>
    /// <param name="ifMatch">The <c>If-Match</c> header as received; may be absent or malformed.</param>
    /// <param name="version">The version the row currently has.</param>
    /// <returns><see langword="true"/> when the header addresses that version.</returns>
    public static bool Matches(string? ifMatch, int version) => ETags.Matches(ifMatch, For(version));

    /// <summary>
    /// The precondition of a bible save (02 README D8/D9): the header must be there and must name the
    /// version the row currently has.
    /// </summary>
    /// <remarks>
    /// The two failures are deliberately different statuses: 428 <c>if_match_required</c> when the header is
    /// absent or blank (the client sent an unconditional save, which this route does not accept), 412
    /// <c>bible_conflict</c> when it names another version, is weak, is <c>*</c> or is malformed (the client's
    /// copy is stale, and the shared rule refuses <c>*</c> everywhere). A 400 would hide both, so the check is
    /// not part of the command validator. The caller runs it after it has established that the series is the
    /// caller's own, so a foreign series answers 404.
    /// </remarks>
    /// <param name="ifMatch">The <c>If-Match</c> header as received.</param>
    /// <param name="current">The version the row currently has.</param>
    /// <returns>The matched version, or the documented error.</returns>
    public static ErrorOr<int> RequireAndMatch(string? ifMatch, int current)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return BibleErrors.IfMatchRequired();
        }

        return Matches(ifMatch, current) ? current : BibleErrors.Conflict();
    }
}
