namespace NightTales.Domain.Research;

/// <summary>The state of a <c>fetch_tasks</c> row (02 D10), stored as snake_case text.</summary>
public enum FetchTaskStatus
{
    /// <summary>Waiting for a lease once <c>not_before</c> has passed.</summary>
    Queued,

    /// <summary>Held by a fetcher until <c>lease_until</c>.</summary>
    Leased,

    /// <summary>Finished with a valid result.</summary>
    Done,

    /// <summary>Finished with an error code.</summary>
    Failed,

    /// <summary>Withdrawn before it finished.</summary>
    Cancelled,
}
