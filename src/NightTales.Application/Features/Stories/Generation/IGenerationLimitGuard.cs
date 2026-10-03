using ErrorOr;

namespace NightTales.Application.Features.Stories.Generation;

/// <summary>
/// The FR-02-7 check (02 README D2, 02.4.2-T1): a rolling window over the episode's counted user-triggered story
/// runs. It is step 5 of <c>GenerateStoryCommand</c>, which has already taken the episode row lock
/// (<c>IAppDbContext.LockEpisodeForUpdateAsync</c>, step 1) inside its unit of work, so the count and the run
/// insert cannot interleave with a concurrent call.
/// </summary>
public interface IGenerationLimitGuard
{
    /// <summary>
    /// Counts the episode's <c>story</c> runs with <c>trigger = user</c> and <c>counts_toward_limit</c> queued after
    /// <c>now − Story:LimitWindowHours</c>.
    /// </summary>
    /// <param name="episodeId">The episode the run would be queued for.</param>
    /// <param name="userId">The owner; the count never crosses tenants.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    /// Success, or <c>story_generation_limit</c> carrying <c>retryAfterSeconds</c> (the seconds until the oldest
    /// counted run leaves the window, at least 1) and <c>limit</c>.
    /// </returns>
    ValueTask<ErrorOr<Success>> CheckAsync(Guid episodeId, Guid userId, CancellationToken cancellationToken);
}
