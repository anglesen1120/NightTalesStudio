using System.Globalization;
using ErrorOr;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Stories;

/// <summary>
/// The story ETag (02 README D9): <c>"sv-&lt;current_story_version_id&gt;"</c>. Unlike the aggregate ETags of
/// <see cref="NightTales.Application.Common.ETags"/> it is derived from the version id, not from <c>xmin</c>, so a
/// no-op save keeps it and every new version changes it. <see cref="RequireAndMatch"/> is the single place the
/// edit and approve commands check the header (02.3.1-T1, 02.3.2-T1).
/// </summary>
public static class StoryEtag
{
    private const string Prefix = "sv-";

    /// <summary>The ETag of a story version, quotes included.</summary>
    /// <param name="storyVersionId">The version the tag names.</param>
    /// <returns>The strong tag, <c>"sv-&lt;id&gt;"</c>.</returns>
    public static string For(Guid storyVersionId) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{Prefix}{storyVersionId:D}\"");

    /// <summary>
    /// Parses a story ETag. Only the strong, quoted <c>"sv-&lt;guid&gt;"</c> a client read is accepted: a weak tag
    /// (<c>W/"…"</c>), a bare guid, a list, <c>*</c> and the tags of other resources (<c>"ep-…"</c>) are not.
    /// </summary>
    /// <param name="header">The <c>If-Match</c> header as received; may be absent.</param>
    /// <param name="storyVersionId">The version id when the header parses; otherwise empty.</param>
    /// <returns><see langword="true"/> when the header is a well-formed story ETag.</returns>
    public static bool TryParse(string? header, out Guid storyVersionId)
    {
        storyVersionId = Guid.Empty;

        if (header is null || header.Length < Prefix.Length + 2)
        {
            return false;
        }

        if (header[0] != '"' || header[^1] != '"')
        {
            return false;
        }

        var inner = header.AsSpan(1, header.Length - 2);
        if (!inner.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParseExact(inner[Prefix.Length..], "D", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        storyVersionId = parsed;
        return true;
    }

    /// <summary>
    /// Checks the <c>If-Match</c> header against the episode's current story version.
    /// </summary>
    /// <param name="ifMatch">The header as received.</param>
    /// <param name="currentStoryVersionId">The episode's current version, or null when it has none.</param>
    /// <returns>
    /// The matched version id; <c>if_match_required</c> (428) for a missing or blank header; <c>story_version_conflict</c>
    /// (412) for a malformed, stale or foreign tag, or when the episode has no current version to match.
    /// </returns>
    public static ErrorOr<Guid> RequireAndMatch(string? ifMatch, Guid? currentStoryVersionId)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return Error.Failure(
                ErrorCodes.IfMatchRequired,
                "An If-Match header with the story ETag you read is required.");
        }

        if (!TryParse(ifMatch, out var parsed) || currentStoryVersionId is not { } current || parsed != current)
        {
            return Error.Failure(
                ErrorCodes.StoryVersionConflict,
                "The story changed since it was read; read it again and retry.");
        }

        return parsed;
    }
}
