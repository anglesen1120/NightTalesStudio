using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts real <c>source_candidates</c> rows for tests that need one (02.7.2-T2): the candidate list, the
/// add route and the dismiss route all read rows a finished search would have written, and
/// <c>source_candidates.generation_run_id</c> is a foreign key to <c>generation_runs</c>, so a seeded
/// candidate must name a real run of the same episode.
/// </summary>
/// <remarks>
/// <para>
/// The row is materialized through the domain factory <see cref="SourceCandidate.Create"/>, so the columns,
/// the rank CHECK and the enum wire value are the production ones. The write runs through
/// <see cref="AppDbContext"/> pinned to the owner's id — the same pin
/// <c>ISystemContext.For(userId)</c> applies in the Worker, expressed here as the
/// <see cref="TestCurrentUser"/> the class database's contexts already take (the shape
/// <see cref="FactSheetBuilder"/> uses).
/// </para>
/// <para>
/// A candidate only exists after a search finished, so the builder queues, starts and completes one
/// <see cref="GenerationKind.FindSources"/> run per candidate unless the caller names an existing run id. It
/// then clears <c>counts_toward_limit</c> again, because the plan states a source search is metered but never
/// counted toward the daily generation limit (02.7 plan §3) and <see cref="GenerationRun.Succeed"/> writes
/// <see langword="true"/> for every kind but <c>bible_proposal</c>.
/// </para>
/// </remarks>
/// <param name="database">The test database.</param>
/// <param name="currentUser">The identity the insert runs as: it must be pinned to the candidate's owner.</param>
/// <param name="clock">The clock of the insert, or <see langword="null"/> for the interceptor's stamp.</param>
public sealed class SourceCandidateBuilder(TestDatabase database, TestCurrentUser currentUser, IClock? clock = null)
{
    /// <summary>The URL a hand-created candidate carries when the caller names none.</summary>
    public const string DefaultUrl = "https://example.com/a-research-article";

    /// <summary>The site name a hand-created candidate carries when the caller names none.</summary>
    public const string DefaultSiteName = "Example";

    /// <summary>
    /// Inserts one proposed <c>source_candidates</c> row of <paramref name="episodeId"/>.
    /// </summary>
    /// <param name="userId">The owner; must own the episode.</param>
    /// <param name="seriesId">The episode's series, used when this call also writes the originating run.</param>
    /// <param name="episodeId">The episode the candidate was proposed for.</param>
    /// <param name="url">The proposed URL as the search reported it.</param>
    /// <param name="normalizedUrl">
    /// The normalized form the unique index <c>ux_source_candidates_episode_url</c> compares; the URL itself
    /// when the caller names none.
    /// </param>
    /// <param name="title">The title the search reported, or <see langword="null"/>.</param>
    /// <param name="siteName">The display name of the site.</param>
    /// <param name="rank">The 1-based rank of the result, 1..10.</param>
    /// <param name="generationRunId">An existing run to attribute the candidate to, or null to write one.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>The inserted candidate, with its generated id and the run it names.</returns>
    public async Task<SourceCandidate> CreateAsync(
        Guid userId,
        Guid seriesId,
        Guid episodeId,
        string url = DefaultUrl,
        string? normalizedUrl = null,
        string? title = null,
        string siteName = DefaultSiteName,
        short rank = 1,
        Guid? generationRunId = null,
        CancellationToken ct = default)
    {
        if (currentUser.Id != userId)
        {
            throw new ArgumentException(
                "The builder's identity must be pinned to the candidate's owner, as ISystemContext.For pins the Worker.",
                nameof(userId));
        }

        await using var context = database.CreateAppDbContext(currentUser, clock);

        var runId = generationRunId ?? (await QueueCompletedFindAsync(context, userId, seriesId, episodeId, ct)).Id;

        var candidate = SourceCandidate.Create(
            userId,
            episodeId,
            runId,
            url,
            normalizedUrl ?? url,
            RegistrableDomainOf(url),
            title,
            siteName,
            rank);

        context.SourceCandidates.Add(candidate);
        await context.SaveChangesAsync(ct);

        return candidate;
    }

    /// <summary>
    /// Writes one completed <c>find_sources</c> run and answers it, so the candidate's foreign key holds. The
    /// run is metered but not counted (<c>counts_toward_limit = false</c>).
    /// </summary>
    private async Task<GenerationRun> QueueCompletedFindAsync(
        AppDbContext context,
        Guid userId,
        Guid seriesId,
        Guid episodeId,
        CancellationToken ct)
    {
        var now = clock?.UtcNow ?? DateTimeOffset.UtcNow;

        var run = GenerationRun.Queue(
            userId,
            seriesId,
            episodeId,
            GenerationKind.FindSources,
            GenerationTrigger.User,
            idempotencyKey: null,
            previousEpisodeStatus: null,
            inputFactSheetId: null,
            reservationId: null,
            worstCaseUsd: 0m,
            now: now,
            countsTowardLimit: false).Value;

        context.GenerationRuns.Add(run);
        run.Start(now);
        run.Succeed(0m, resultStoryVersionId: null, resultFactSheetId: null, now: now);
        await context.SaveChangesAsync(ct);

        // Succeed() writes true for every kind but bible_proposal; the plan keeps a source search out of the
        // daily generation limit, so the seeded row carries the flag the handler writes.
        await context.GenerationRuns
            .Where(row => row.Id == run.Id)
            .ExecuteUpdateAsync(stamp => stamp.SetProperty(row => row.CountsTowardLimit, false), ct);

        return run;
    }

    /// <summary>
    /// The registrable domain a candidate row carries: the URL's host, lowercased. The tests never assert the
    /// public-suffix reduction itself (02.5.1's normalization owns it); the column is only carried through.
    /// </summary>
    private static string RegistrableDomainOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.Host.ToLowerInvariant()
            : string.Empty;
}
