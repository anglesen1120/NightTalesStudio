namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// One past episode of the series as the idea prompt sees it inside the data object
/// (<c>recent_episodes[{title, summary}]</c>, 02.8.1-T1 plan §7.1): a title and an optional summary, never an id
/// (D6's alias principle) and never a URL or a source title (D19).
/// </summary>
/// <remarks>
/// The prompt's own continuity memory is <c>bible.episode_summaries</c>, the writer's view of the same history,
/// so this record is only the fallback the planner fills when the bible is empty or absent (task file
/// [DECISION]): the structure history carries titles, not summaries, which is why <see cref="Summary"/> may be
/// <see langword="null"/>.
/// </remarks>
/// <param name="Title">The approved episode's title.</param>
/// <param name="Summary">The one-line summary, or <see langword="null"/> when only the title is known.</param>
public sealed record EpisodeSummaryRef(string Title, string? Summary);
