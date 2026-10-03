namespace NightTales.Domain.Research;

/// <summary>What a <c>story_sources</c> row is (02.5.1 plan §3.2), stored as snake_case text: <c>url</c> or <c>user_note</c>.</summary>
public enum StorySourceKind
{
    /// <summary>A pasted article or record page that the fetcher downloads.</summary>
    Url,

    /// <summary>The user's own note: it has no fetch, its text lives in the row.</summary>
    UserNote,
}
