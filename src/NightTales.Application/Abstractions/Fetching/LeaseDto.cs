namespace NightTales.Application.Abstractions.Fetching;

/// <summary>
/// One leased task as the fetcher receives it (02.5.2-T2, plan §5.2): the body of an item of
/// <c>POST /internal/fetcher/v1/tasks/lease</c>.
/// </summary>
/// <param name="TaskId">The <c>fetch_tasks</c> id.</param>
/// <param name="Kind">The wire kind, <c>fetch_source</c> or <c>find_sources</c>.</param>
/// <param name="Url">The URL to download; <see langword="null"/> for <c>find_sources</c>.</param>
/// <param name="Attempt">The attempt this lease counts, starting at 1.</param>
/// <param name="LeaseUntil">When the lease expires without a heartbeat.</param>
/// <param name="UserAgent">The crawler user agent the fetcher must send (02 D11).</param>
/// <param name="FinderRequest">The requester payload of a <c>find_sources</c> task; <see langword="null"/> otherwise.</param>
public sealed record LeaseDto(
    Guid TaskId,
    string Kind,
    string? Url,
    int Attempt,
    DateTimeOffset LeaseUntil,
    string UserAgent,
    FinderRequestDto? FinderRequest);

/// <summary>
/// The requester payload of a <c>find_sources</c> task (02.5.2-T2 plan §5.2), re-derived at lease time from the
/// <c>generation_runs</c> row of the task so no brief text is duplicated into the fetcher-visible table.
/// </summary>
/// <param name="Topic">The series topic, wire snake_case.</param>
/// <param name="Language">The series language, wire code.</param>
/// <param name="SeriesTitle">The series title.</param>
/// <param name="Brief">The episode brief.</param>
/// <param name="AllowList">The registrable domains the finder may propose (02.7.2-T1); empty until 02.7 ships.</param>
/// <param name="MaxCandidates">The most candidates the finder should return.</param>
public sealed record FinderRequestDto(
    string Topic,
    string Language,
    string SeriesTitle,
    string Brief,
    IReadOnlyList<string> AllowList,
    int MaxCandidates);
