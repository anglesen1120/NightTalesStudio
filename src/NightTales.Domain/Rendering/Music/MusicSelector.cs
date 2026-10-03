using System.Buffers.Binary;
using System.Security.Cryptography;
using NightTales.Domain.Series;

namespace NightTales.Domain.Rendering.Music;

/// <summary>What the selector needs from a render submit (05 README D8).</summary>
/// <param name="Topic">The series topic; it decides the candidate moods.</param>
/// <param name="RecentTrackIds">The series' latest picks, newest first (<c>IRecentMusicQuery</c>).</param>
/// <param name="EpisodeId">The episode being rendered; it seeds the deterministic pick.</param>
public sealed record MusicSelectionInput(Topic Topic, IReadOnlyList<string> RecentTrackIds, Guid EpisodeId);

/// <summary>The picked track: what <c>render-params.v1</c> stores and the D3 <c>bgm_*</c> keys carry.</summary>
/// <param name="TrackId">The catalog id.</param>
/// <param name="File">The audio file name.</param>
/// <param name="BgmVolume">The gain.</param>
public sealed record MusicChoice(string TrackId, string File, decimal BgmVolume);

/// <summary>
/// The pure, deterministic pick of 05 README D8 (M7). The mood and topic maps and the index formula are the
/// contract: changing them is a contract change, not an implementation detail.
/// </summary>
public static class MusicSelector
{
    /// <summary>The default number of recent tracks the pick avoids (<c>Music:AvoidRecent</c>, read by 05.1.2-T1).</summary>
    public const int DefaultAvoidRecent = 3;

    /// <summary>
    /// Picks a track, or <c>null</c> when nothing qualifies (no music, hint <c>music_none</c>, D10).
    /// </summary>
    /// <remarks>
    /// Candidates are the <paramref name="active"/> tracks whose moods intersect the topic's moods and whose
    /// scope is <c>any</c> or <c>youtube</c> (07 enforces the platform rule, CR-8). The first
    /// <paramref name="avoidRecent"/> ids of <see cref="MusicSelectionInput.RecentTrackIds"/> (newest first) are
    /// dropped, unless that would leave nothing. The rest are ordered by <c>TrackId</c> (ordinal) and indexed
    /// by the first four bytes of SHA-256 over the episode id's 16 bytes, big-endian, modulo the count —
    /// never <c>GetHashCode</c>, which is randomised per process.
    /// </remarks>
    /// <param name="input">The topic, the recent picks and the episode.</param>
    /// <param name="active">The catalog; tracks with <c>Active = false</c> are ignored.</param>
    /// <param name="avoidRecent">How many of the newest recent picks to avoid.</param>
    public static MusicChoice? Pick(
        MusicSelectionInput input,
        IReadOnlyList<MusicTrack> active,
        int avoidRecent = DefaultAvoidRecent)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(active);

        var topicMoods = MusicTrackRules.TopicMoods(input.Topic);
        var candidates = active
            .Where(track => track.Active
                && track.PlatformScope is MusicPlatformScope.Any or MusicPlatformScope.YouTube
                && track.Moods.Any(mood => topicMoods.Contains(mood, StringComparer.Ordinal)))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var recent = input.RecentTrackIds
            .Take(Math.Max(avoidRecent, 0))
            .ToHashSet(StringComparer.Ordinal);
        var fresh = candidates.Where(track => !recent.Contains(track.TrackId)).ToList();
        if (fresh.Count > 0)
        {
            candidates = fresh;
        }

        candidates.Sort((left, right) => string.CompareOrdinal(left.TrackId, right.TrackId));

        var hash = SHA256.HashData(input.EpisodeId.ToByteArray());
        var index = (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % (uint)candidates.Count);
        var picked = candidates[index];

        return new MusicChoice(picked.TrackId, picked.FileName, picked.BgmVolume);
    }
}
