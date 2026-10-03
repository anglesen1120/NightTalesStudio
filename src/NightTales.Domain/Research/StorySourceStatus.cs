namespace NightTales.Domain.Research;

/// <summary>Where a source is in its life (02.5.1 plan §3.2), stored as snake_case text.</summary>
public enum StorySourceStatus
{
    /// <summary>A fetch task is queued or running.</summary>
    Pending,

    /// <summary>The text is available: a fetched page or a user note.</summary>
    Fetched,

    /// <summary>The fetch ended with a D8 job outcome code.</summary>
    Failed,

    /// <summary>The fetcher rejected a redirect hop (blocked domain); never written by the add command.</summary>
    Rejected,
}
