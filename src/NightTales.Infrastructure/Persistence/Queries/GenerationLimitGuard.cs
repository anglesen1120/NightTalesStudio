using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Queries;

/// <summary>
/// The <see cref="IGenerationLimitGuard"/> over <c>generation_runs</c> (02 README D2, 02.4.2-T1): one round trip
/// returns the count and the oldest <c>queued_at</c> of the episode's counted user-triggered story runs inside the
/// rolling window, served by <c>ix_generation_runs_episode_kind_queued</c>.
/// </summary>
/// <remarks>
/// <para>
/// The window edge is exclusive: a run queued exactly <c>LimitWindowHours</c> ago has left the window
/// (<c>queued_at &gt; now − window</c>). The reading of <c>now</c> is the application clock's, the same clock
/// that stamps <c>queued_at</c> in <c>GenerationRun.Queue</c>, so both sides of the comparison come from one source
/// and the boundary is testable without clock games; the task's note on <c>now()</c> in SQL is superseded by that
/// (a run stamped by this host and compared against another clock is what would skew).
/// </para>
/// <para>
/// It is scoped to the caller's unit of work: it runs on the same <see cref="IAppDbContext"/> (hence the same
/// transaction) that already holds the episode row lock, and the tenant filter plus the explicit user predicate
/// keep the count inside one owner.
/// </para>
/// </remarks>
/// <param name="db">The context of the command's unit of work.</param>
/// <param name="options">The bound <c>Story</c> section.</param>
/// <param name="clock">The application clock.</param>
public sealed class GenerationLimitGuard(IAppDbContext db, IOptions<GenerationLimitOptions> options, IClock clock)
    : IGenerationLimitGuard
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> CheckAsync(Guid episodeId, Guid userId, CancellationToken cancellationToken)
    {
        var limit = options.Value.DailyGenerationLimit;
        var window = TimeSpan.FromHours(options.Value.LimitWindowHours);
        var now = clock.UtcNow;

        // SEC-1 fail-closed: the D10 tenant filter silently drops every row when the ambient user is anonymous or
        // differs from the caller's, so an unreadable count must never look like "under the limit". The precedent
        // is LockEpisodeForUpdateAsync (AppDbContext), which likewise refuses to act outside the caller's scope.
        if (db is not ITenantFilterContext tenant || tenant.CurrentUserId != userId)
        {
            return Error.Failure(
                ErrorCodes.InternalError,
                "The generation limit check ran outside the caller's tenant scope.");
        }

        var windowStart = now - window;

        // GroupBy over a constant is the one-round-trip, two-aggregate shape (plan §4 U6); an empty window
        // yields no group at all, so `oldest` is never read from NULL.
        var aggregate = await db.GenerationRuns
            .AsNoTracking()
            .Where(run => run.EpisodeId == episodeId
                && run.UserId == userId
                && run.Kind == GenerationKind.Story
                && run.Trigger == GenerationTrigger.User
                && run.CountsTowardLimit
                && run.QueuedAt > windowStart)
            .GroupBy(_ => 1)
            .Select(group => new { Counted = group.Count(), Oldest = group.Min(run => run.QueuedAt) })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (aggregate is null || aggregate.Counted < limit)
        {
            return Result.Success;
        }

        return Error.Failure(
            ErrorCodes.StoryGenerationLimit,
            $"{limit} story generations per rolling {options.Value.LimitWindowHours} h are allowed for one episode.",
            new Dictionary<string, object>
            {
                [ErrorMetadata.RetryAfterSeconds] = RetryAfterSeconds(aggregate.Oldest, window, now),
                [ErrorMetadata.Limit] = limit,
            });
    }

    /// <summary>
    /// The whole seconds until <paramref name="oldest"/> leaves the window, rounded up and at least 1, so a client
    /// that waits that long finds the run outside the window (it must be strictly older than the window).
    /// </summary>
    private static int RetryAfterSeconds(DateTimeOffset oldest, TimeSpan window, DateTimeOffset now)
    {
        var remaining = oldest + window - now;
        var seconds = (remaining.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;

        return (int)Math.Max(1, seconds);
    }
}
