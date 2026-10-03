namespace NightTales.Domain.Research;

/// <summary>The unit of work a fetcher leases (02 D10), stored as snake_case text.</summary>
public enum FetchTaskKind
{
    /// <summary>Download and extract one source URL.</summary>
    FetchSource,

    /// <summary>The episode-level source finder (02.7); no source row yet.</summary>
    FindSources,
}
