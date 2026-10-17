using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Infrastructure.OpenAI;

namespace NightTales.Infrastructure.Autopilot;

/// <summary>
/// Reads the context of one idea-generation run (02.8.1-T1 plan §4 U1 step 1): the series' topic and language,
/// the owner's tone and banned themes, the bible snapshot, the structure history and the owner's research notes.
/// </summary>
/// <remarks>
/// <para>
/// Tenant isolation (02.8.1 plan §8, 09 README D10): every query runs on the scope's read model, whose global
/// filter restricts it to the caller's own rows; a series is therefore only visible to its owner. The planner
/// resolves the series first and maps a foreign or unknown id to <c>NotFound</c>, so a row that vanished between
/// those two reads is data drift here, not a user input: this provider throws instead of silently proposing for
/// nothing.
/// </para>
/// <para>
/// <c>tone</c> and <c>banned_themes</c> live in <c>series.autopilot_settings</c>, whose shape 02.8.3-T1 owns (TL
/// ruling #3). Until that schema lands — and for any row written before it — the keys are absent: the read is
/// defensive, a missing key is <see langword="null"/>/empty, a malformed document is treated as absent, and
/// neither is ever an error. Values are capped to the settings contract's own bounds (tone 120 characters, 20
/// banned themes of 60) so a hand-written row cannot grow the prompt without bound.
/// </para>
/// <para>
/// D19: no note text, bible text or title is logged or carried anywhere but the context the planner turns into
/// the one data object. The extracted text of a URL source is never read; a URL source contributes nothing at
/// all (see the type's notes on research notes).
/// </para>
/// </remarks>
/// <param name="readDb">The tenant-filtered read model of the caller's scope.</param>
/// <param name="bibles">The 02.6.3-T3 snapshot provider, so ideas see the same budgeted bible the writer does.</param>
/// <param name="history">The series' approved-episode structure history.</param>
/// <param name="options">The <c>Autopilot:Ideas</c> section: the requested candidate count.</param>
public sealed class IdeaContextProvider(
    IReadDb readDb,
    IBibleSnapshotProvider bibles,
    IEpisodeStructureHistory history,
    IOptions<IdeaOptions> options) : IIdeaContextProvider
{
    /// <summary>The newest approved episodes the structure history carries (plan §3.5's windows fit well inside).</summary>
    public const int HistoryCount = 50;

    /// <summary>The most sources of the series the notes block carries (plan §4 U1).</summary>
    public const int MaxResearchNotes = 5;

    /// <summary>The longest research note text the prompt carries (plan §8, D19).</summary>
    public const int MaxResearchNoteChars = 400;

    /// <summary>The longest tone the settings contract accepts (02.8.3-T1, TL ruling #3).</summary>
    public const int MaxToneChars = 120;

    /// <summary>The most banned themes the settings contract accepts (02.8.3-T1, TL ruling #3).</summary>
    public const int MaxBannedThemes = 20;

    /// <summary>The longest banned theme the settings contract accepts (02.8.3-T1, TL ruling #3).</summary>
    public const int MaxBannedThemeChars = 60;

    /// <inheritdoc />
    public async ValueTask<IdeaContext> GetAsync(Guid seriesId, CancellationToken ct)
    {
        var series = await readDb.Query<Series>()
            .Where(row => row.Id == seriesId)
            .Select(row => new SeriesContext(row.Topic, row.Language, row.AutopilotSettings))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (series is null)
        {
            // The planner checks the series first (foreign/unknown => NotFound); a row missing here is drift.
            throw new InvalidOperationException(
                $"Series {seriesId} was not found for the caller when the idea context was read; the planner checks it first.");
        }

        var bible = await bibles.GetForWriterAsync(seriesId, ct).ConfigureAwait(false);
        var structure = await history.GetRecentAsync(seriesId, HistoryCount, ct).ConfigureAwait(false);
        var notes = await ReadNotesAsync(seriesId, ct).ConfigureAwait(false);

        var (tone, bannedThemes) = ReadAutopilotSettings(series.AutopilotSettings);
        var maxCount = Math.Max(1, options.Value.MaxCount);
        var count = Math.Clamp(options.Value.Count, 1, maxCount);

        return new IdeaContext(
            SnakeCase(series.Topic.ToString()),
            SnakeCase(series.Language.ToString()),
            tone,
            bannedThemes,
            bible.Bible,
            structure,
            notes,
            count);
    }

    /// <summary>
    /// The owner's newest notes: user notes only (README D19 and the acceptance check forbid a source title or
    /// URL in the data object, so a URL source contributes nothing), newest first, at most five.
    /// </summary>
    private async ValueTask<IReadOnlyList<ResearchNoteRef>> ReadNotesAsync(Guid seriesId, CancellationToken ct)
    {
        var episodeIds = readDb.Query<Episode>()
            .Where(episode => episode.SeriesId == seriesId)
            .Select(episode => episode.Id);

        var texts = await readDb.Query<StorySource>()
            .Where(source => source.Kind == StorySourceKind.UserNote
                && source.NoteText != null
                && episodeIds.Contains(source.EpisodeId))
            .OrderByDescending(source => source.CreatedAt)
            .Take(MaxResearchNotes)
            .Select(source => source.NoteText!)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var kind = SnakeCase(nameof(StorySourceKind.UserNote));
        var notes = new List<ResearchNoteRef>(texts.Count);
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            notes.Add(new ResearchNoteRef(kind, IdeaDataObjectJson.TruncateNote(text, MaxResearchNoteChars)));
        }

        return notes;
    }

    /// <summary>
    /// Reads <c>tone</c> and <c>banned_themes</c> from the series' settings document; absent or malformed values
    /// are <see langword="null"/>/empty (TL ruling #3: never an error).
    /// </summary>
    private static (string? Tone, IReadOnlyList<string> BannedThemes) ReadAutopilotSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, []);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, []);
            }

            var tone = root.TryGetProperty("tone", out var toneElement) && toneElement.ValueKind == JsonValueKind.String
                ? toneElement.GetString()
                : null;

            var bannedThemes = new List<string>();
            if (root.TryGetProperty("banned_themes", out var themesElement) && themesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in themesElement.EnumerateArray())
                {
                    if (bannedThemes.Count == MaxBannedThemes)
                    {
                        break;
                    }

                    if (element.ValueKind == JsonValueKind.String
                        && element.GetString() is { } theme
                        && !string.IsNullOrWhiteSpace(theme))
                    {
                        bannedThemes.Add(IdeaDataObjectJson.TruncateNote(theme.Trim(), MaxBannedThemeChars));
                    }
                }
            }

            return (
                string.IsNullOrWhiteSpace(tone) ? null : IdeaDataObjectJson.TruncateNote(tone.Trim(), MaxToneChars),
                bannedThemes);
        }
        catch (JsonException)
        {
            // A malformed settings document before 02.8.3-T1 validates writes: absent, never an error (TL #3).
            return (null, []);
        }
    }

    /// <summary>The snake_case wire spelling the data object's control values use (for example <c>true_crime</c>).</summary>
    private static string SnakeCase(string member) => JsonNamingPolicy.SnakeCaseLower.ConvertName(member);

    /// <summary>The columns the context needs, so the bible and the settings document are read once.</summary>
    /// <param name="Topic">The series' narrative topic.</param>
    /// <param name="Language">The series' content language.</param>
    /// <param name="AutopilotSettings">The settings document 02.8.3-T1 owns the shape of, or <see langword="null"/>.</param>
    private sealed record SeriesContext(Topic Topic, Language Language, string? AutopilotSettings);
}
