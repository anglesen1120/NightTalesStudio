using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts the <c>story_versions</c> row and the <c>render_jobs</c> row that references it, carrying the
/// <c>music.attribution</c> of <c>render-params.v1</c> — the one string 06.2.1-T1 appends to a publication
/// description (06 README F08: the attribution is frozen in the render's params, not read from a catalog).
/// </summary>
/// <remarks>
/// <para>
/// The job goes in through <c>AppDbContext</c>'s change tracker (<see cref="ShellInstance"/>), so the
/// <c>params</c> column is written by the same <c>RenderParamsJsonConverter</c> production uses — a document
/// this builder wrote is one the handler can read back.
/// </para>
/// <para>
/// The <c>story_versions</c> parent goes in with raw SQL: it belongs to 02's behaviour lane, no builder owns
/// it, and the job's composite same-owner foreign key needs it to exist. Only the columns that lane requires
/// are filled (the <c>RecentMusicQueryTests</c> precedent).
/// </para>
/// </remarks>
public sealed class RenderJobBuilder(TestDatabase database, TestCurrentUser currentUser, IClock? clock = null)
{
    /// <summary>The attribution a seeded track carries unless a test names one.</summary>
    public const string DefaultAttribution = "Đêm tĩnh — NTS Test Artist";

    /// <summary>The style prompt version a seeded job pins.</summary>
    public const string DefaultStylePromptVersion = "style=nts-test@00000000;neg=v1";

    /// <summary>Inserts a <c>succeeded</c> job whose params carry <paramref name="attribution"/>.</summary>
    /// <param name="withMusic">
    /// <c>false</c> seeds <c>music: null</c> — the no-music render, whose publication gets no attribution tail.
    /// </param>
    /// <returns>The new <c>render_jobs.id</c>.</returns>
    public async Task<Guid> CreateAsync(
        Guid userId,
        Guid episodeId,
        string? attribution = DefaultAttribution,
        bool withMusic = true,
        DateTimeOffset? submittedAt = null,
        CancellationToken ct = default)
    {
        var storyVersionId = await SeedStoryVersionAsync(userId, episodeId, ct);
        var jobId = Guid.CreateVersion7();
        var started = submittedAt ?? clock?.UtcNow ?? DateTimeOffset.UtcNow;

        await using var context = database.CreateAppDbContext(currentUser, clock);

        var job = ShellInstance.Create<RenderJob>();
        context.Add(job);

        ShellInstance.Set(context, job, nameof(RenderJob.Id), jobId);
        ShellInstance.Set(context, job, nameof(RenderJob.UserId), userId);
        ShellInstance.Set(context, job, nameof(RenderJob.EpisodeId), episodeId);
        ShellInstance.Set(context, job, nameof(RenderJob.StoryVersionId), storyVersionId);
        ShellInstance.Set(context, job, nameof(RenderJob.ReservationId), Guid.CreateVersion7());
        ShellInstance.Set(context, job, nameof(RenderJob.WorstCaseUsd), 0.25m);
        ShellInstance.Set(context, job, nameof(RenderJob.Status), RenderJobStatus.Succeeded);
        ShellInstance.Set(context, job, nameof(RenderJob.Stage), "done");
        ShellInstance.Set(context, job, nameof(RenderJob.Percent), (short)100);
        ShellInstance.Set(context, job, nameof(RenderJob.Params), Params(storyVersionId, attribution, withMusic));
        ShellInstance.Set(context, job, nameof(RenderJob.Attempt), 1);
        ShellInstance.Set(context, job, nameof(RenderJob.StartedAt), started);
        ShellInstance.Set(context, job, nameof(RenderJob.SubmittedAt), started);
        ShellInstance.Set(context, job, nameof(RenderJob.FinishedAt), started.AddMinutes(4));

        await context.SaveChangesAsync(ct);

        return jobId;
    }

    /// <summary>The <c>render-params.v1</c> document the handler reads its attribution from (D11 order).</summary>
    private static RenderParamsV1 Params(Guid storyVersionId, string? attribution, bool withMusic)
        => new(
            RenderParamsV1.CurrentMapperVersion,
            storyVersionId,
            "landscape",
            "vi",
            "kokoro:nts-test-voice",
            Guid.CreateVersion7(),
            DefaultStylePromptVersion,
            new ImagePlanSummary(5, 3, 6, 3, 0, [], [], DefaultStylePromptVersion),
            new SubtitlesParams(true, "landscape@1"),
            withMusic ? new MusicParams("catalog-track-nts-test", 0.2m, attribution) : null,
            5);

    /// <summary>Inserts the <c>story_versions</c> row the job's composite same-owner FK needs.</summary>
    private async Task<Guid> SeedStoryVersionAsync(Guid userId, Guid episodeId, CancellationToken ct)
    {
        var versionId = Guid.CreateVersion7();

        await database.ExecuteAsync(
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, created_at) VALUES ("
            + $"'{versionId}', '{userId}', '{episodeId}', 1, 'edited', 'NTS Test Story', "
            + "'Nội dung thử nghiệm.', ARRAY[]::text[], 1, '{\"schema_version\":\"story.v1\"}'::jsonb, now())",
            ct);

        return versionId;
    }
}
