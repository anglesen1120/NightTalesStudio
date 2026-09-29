using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts real <c>episodes</c> rows (09 README D13, 09 W3 CR-41) for an existing series. The status is
/// a parameter, because the state tests of 09.5.1-T1 and 02 drive episodes from every state.
/// </summary>
/// <remarks>
/// The row goes through <see cref="AppDbContext"/> like production code, so the FKs, the enum wire
/// values and the audit stamps are exactly the ones a real request writes.
/// </remarks>
public sealed class EpisodeBuilder(TestDatabase database, TestCurrentUser currentUser, IClock? clock = null)
{
    /// <summary>The default brief the builder writes.</summary>
    public const string DefaultBrief = "Một tập thử nghiệm cho kiểm thử.";

    /// <summary>
    /// Inserts one episode of <paramref name="seriesId"/>. <paramref name="userId"/> must own the series;
    /// <paramref name="createdAt"/> defaults to the interceptor stamp.
    /// </summary>
    public async Task<Episode> CreateAsync(
        Guid userId,
        Guid seriesId,
        string brief = DefaultBrief,
        string? title = null,
        LengthTier lengthTier = LengthTier.Standard,
        EpisodeStatus status = EpisodeStatus.Draft,
        DateTimeOffset? createdAt = null,
        CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();

        await using var context = database.CreateAppDbContext(currentUser, clock);

        var episode = ShellInstance.Create<Episode>();
        context.Episodes.Add(episode);

        ShellInstance.Set(context, episode, nameof(Episode.Id), id);
        ShellInstance.Set(context, episode, nameof(Episode.UserId), userId);
        ShellInstance.Set(context, episode, nameof(Episode.SeriesId), seriesId);
        ShellInstance.Set(context, episode, nameof(Episode.Title), title);
        ShellInstance.Set(context, episode, nameof(Episode.Brief), brief);
        ShellInstance.Set(context, episode, nameof(Episode.LengthTier), lengthTier);
        ShellInstance.Set(context, episode, nameof(Episode.Status), status);

        if (createdAt is { } stamp)
        {
            ShellInstance.Set(context, episode, nameof(Episode.CreatedAt), stamp);
            ShellInstance.Set(context, episode, nameof(Episode.UpdatedAt), stamp);
        }

        await context.SaveChangesAsync(ct);

        return episode;
    }
}
