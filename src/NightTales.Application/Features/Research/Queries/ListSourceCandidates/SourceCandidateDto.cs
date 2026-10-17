namespace NightTales.Application.Features.Research.Queries.ListSourceCandidates;

/// <summary>
/// One find-run candidate on the wire (02.7.2-T2 R9). <see cref="Url"/> is the URL the finder stored; the extracted
/// page text is never part of it. <see cref="SourceId"/> is set only for an <c>added</c> candidate.
/// </summary>
/// <param name="Id">The candidate id the add and dismiss routes take.</param>
/// <param name="Url">The candidate's URL as the finder recorded it.</param>
/// <param name="Title">The page title the finder read, or <see langword="null"/>.</param>
/// <param name="SiteName">The site's display name.</param>
/// <param name="Rank">The finder's 1..10 ordering within its run.</param>
/// <param name="Status">The wire status: <c>proposed</c>, <c>added</c> or <c>dismissed</c>.</param>
/// <param name="SourceId">The source the candidate became, once added.</param>
/// <param name="CreatedAt">When the find run recorded the candidate.</param>
public sealed record SourceCandidateDto(
    Guid Id,
    string Url,
    string? Title,
    string SiteName,
    short Rank,
    string Status,
    Guid? SourceId,
    DateTimeOffset CreatedAt);
