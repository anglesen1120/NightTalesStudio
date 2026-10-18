using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The seam <see cref="PollRenderJob"/> hands a committed <c>ingesting</c> row to (09.6.3-T1, WT45): the real
/// registration of 09.6.3-T2 resolves to <see cref="SchedulerRenderIngestEnqueuer"/>, and one call on a row in
/// <c>ingesting</c> reaches the job port as <see cref="IngestRenderOutputJob"/> with that row's id and nothing
/// else (D12, ids-only arguments).
/// </summary>
/// <remarks>
/// The poller's half — the tick that commits <c>ingesting</c> and then calls this seam once with the row's id —
/// is <c>PollRenderJobTests.Terminal_Succeeded_EnqueuesIngest</c>. This test covers the half 09.6.3-T2 replaced:
/// what the real enqueuer does with that id, asserted on the scheduling call the port receives rather than on the
/// ingest job's own behaviour (09.6.3-T2 owns the job class; its behaviour is its own tests' subject).
/// </remarks>
public sealed class RenderIngestEnqueuerTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enqueue_SchedulesTheIngestJob_WithTheCommittedRowsId()
    {
        var rowId = await SeedIngestingRowAsync();
        var scheduler = new RecordingJobScheduler();
        await using var host = await StartHostAsync(scheduler);

        using var scope = host.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRenderIngestEnqueuer>().Enqueue(rowId);

        // The row really is the committed `ingesting` one the poller leaves behind (not a random value).
        SqlProbe.ScalarText(Database, $"SELECT status FROM render_jobs WHERE id = '{rowId}'").ShouldBe("ingesting");

        var enqueued = scheduler.Enqueued.ShouldHaveSingleItem();
        enqueued.JobType.ShouldBe(typeof(IngestRenderOutputJob));
        enqueued.Queue.ShouldBe(JobQueues.Name(JobQueue.Render), "the queue the Worker's server already serves");
        enqueued.RenderJobId.ShouldBe(rowId, "the id the seam was handed is the id the job port receives");
    }

    [Fact]
    public async Task Container_ResolvesTheSchedulerBackedEnqueuer_AndNothingElse()
    {
        var scheduler = new RecordingJobScheduler();
        await using var host = await StartHostAsync(scheduler);

        host.Services.GetRequiredService<IRenderIngestEnqueuer>().ShouldBeOfType<SchedulerRenderIngestEnqueuer>();
        host.Services.GetServices<IRenderIngestEnqueuer>().Count().ShouldBe(1, "one seam, no second registration");

        // The 09.6.3-T1 default warned and enqueued nothing; 09.6.3-T2 replaced it rather than registering a
        // second seam, so the scheduler-backed class is the only implementation of the port in Application.
        typeof(IRenderIngestEnqueuer).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsNested: false }
                && typeof(IRenderIngestEnqueuer).IsAssignableFrom(type))
            .ShouldHaveSingleItem("the scheduler-backed class is the only implementation of the port")
            .ShouldBe(typeof(SchedulerRenderIngestEnqueuer));
    }

    [Fact]
    public async Task Enqueue_CarriesTheIdAlone()
    {
        var scheduler = new RecordingJobScheduler();
        await using var host = await StartHostAsync(scheduler);

        using var scope = host.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRenderIngestEnqueuer>().Enqueue(Guid.CreateVersion7());

        var call = scheduler.Enqueued.ShouldHaveSingleItem().Body;
        call.Method.Name.ShouldBe(nameof(IngestRenderOutputJob.RunAsync));
        call.Arguments.Count.ShouldBe(2, "the job id and the cancellation token, never the params or the node (D12)");
        call.Arguments[0].Type.ShouldBe(typeof(Guid));

        // The job's own signature is the other half of the ids-only rule: a job argument is stored in plain
        // text in the `hangfire` schema, so nothing but the id may be passed.
        typeof(IngestRenderOutputJob)
            .GetMethod(nameof(IngestRenderOutputJob.RunAsync))!
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ShouldBe(
                new[] { typeof(Guid), typeof(CancellationToken) },
                "the id and the token only, never the params or the node");
    }

    /// <summary>A started Worker host whose job port records instead of scheduling (09 README D13).</summary>
    private async Task<WorkerHost> StartHostAsync(RecordingJobScheduler scheduler)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobScheduler>();
                services.AddSingleton<IJobScheduler>(scheduler);
            });

        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>
    /// One committed <c>ingesting</c> render job: the state the poller's success tick leaves behind, copied from
    /// <c>PollRenderJobTests</c>'s seeded world — an owner, a series, an episode in <c>rendering</c>, its story
    /// version, a worker node and the row. The ingest itself never reads it here: it is the id this seam has to
    /// hand over.
    /// </summary>
    private async Task<Guid> SeedIngestingRowAsync()
    {
        var actor = new TestCurrentUser();
        var clock = new FakeClock(Start);
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(user.Id, series.Id, ct: Ct);

        var draft = Draft();
        var version = StoryVersion.Create(
            user.Id,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Start);

        await using (var seed = Database.CreateAppDbContext(actor, clock))
        {
            seed.StoryVersions.Add(version);
            await seed.SaveChangesAsync(Ct);
        }

        var nodeId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();

        await Database.ExecuteAsync($"UPDATE episodes SET status = 'rendering' WHERE id = '{episode.Id}'", Ct);
        await Database.ExecuteAsync(
            "INSERT INTO worker_nodes (id, name, base_url, capacity, state, last_heartbeat_at, gpu_model, "
            + "tls_cert_sha256, created_at, updated_at) "
            + $"VALUES ('{nodeId}', 'ingest-node-{nodeId:N}', 'https://127.0.0.1:8443', 2, 'active', now(), "
            + "'RTX 4090', '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF', now(), now())",
            Ct);
        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, adapter_task_id, worker_node_id, submitted_at, started_at) "
            + $"VALUES ('{jobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{Guid.CreateVersion7()}', "
            + $"'ingesting', 'done', 100, '{ParamsJson(version.Id)}'::jsonb, 1, '{taskId}', '{nodeId}', "
            + $"'{Start.AddMinutes(-10):O}', '{Start.AddMinutes(-1):O}')",
            Ct);

        return jobId;
    }

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): every member is <c>[JsonRequired]</c>, so a row the
    /// ingest could load carries one.
    /// </summary>
    private static string ParamsJson(Guid storyVersionId) =>
        JsonSerializer.Serialize(new RenderParamsV1(
            RenderParamsV1.CurrentMapperVersion,
            storyVersionId,
            "landscape",
            "vi",
            "kokoro:test",
            Guid.CreateVersion7(),
            "style=test@0123abcd;neg=v1",
            new ImagePlanSummary(3, 1, 2, 1, 0, [], ["paragraph:1"], "style=test@0123abcd;neg=v1"),
            new SubtitlesParams(true, "landscape@1"),
            null,
            3));

    /// <summary>The smallest clean story document the version factory accepts; its text is never rendered here.</summary>
    private static StoryContentV1 Draft() => new(
        "The pier at dawn",
        new StorySegmentV1("Grey mist drifted over the riverside.", ["quiet riverside morning"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>
    /// The job port of this test: <see cref="Enqueue{TJob}"/> records the call it was handed and never runs it, so
    /// the assertion is about what the seam asked the Worker to do.
    /// </summary>
    private sealed class RecordingJobScheduler : IJobScheduler
    {
        /// <summary>Every enqueue of this host, in order.</summary>
        public List<RecordedEnqueue> Enqueued { get; } = [];

        /// <summary>Records the call and returns a job id; the job itself is never instantiated or run.</summary>
        public string Enqueue<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall)
            where TJob : class, IBackgroundJob
        {
            ArgumentNullException.ThrowIfNull(methodCall);

            Enqueued.Add(RecordedEnqueue.Of(methodCall));

            return Guid.CreateVersion7().ToString("N");
        }

        /// <inheritdoc />
        public string Schedule<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall, TimeSpan delay)
            where TJob : class, IBackgroundJob =>
            throw new NotSupportedException("The ingest is enqueued once the row is committed, never scheduled.");

        /// <summary>
        /// The recurring half belongs to the Worker's own registrar, which this host starts; the ingest never
        /// upserts a recurring job, so the call is accepted and dropped.
        /// </summary>
        public void AddOrUpdateRecurring<TJob>(
            string id,
            string cron,
            Expression<Func<TJob, CancellationToken, Task>> methodCall)
            where TJob : class, IBackgroundJob
        {
        }

        /// <summary>Accepted and dropped, for the same reason as <see cref="AddOrUpdateRecurring{TJob}"/>.</summary>
        public void RemoveRecurringIfExists(string id)
        {
        }
    }

    /// <summary>One recorded <see cref="IJobScheduler.Enqueue{TJob}"/> call: its job type and its own expression.</summary>
    private sealed record RecordedEnqueue(Type JobType, LambdaExpression Call)
    {
        /// <summary>The method call the port would have performed.</summary>
        public MethodCallExpression Body => (MethodCallExpression)Call.Body;

        /// <summary>The queue the job's own <see cref="JobPolicyAttribute"/> selects (09 README D12).</summary>
        public string Queue =>
            JobQueues.Name(JobType.GetCustomAttribute<JobPolicyAttribute>()?.Queue ?? JobQueue.Default);

        /// <summary>
        /// The <c>render_jobs</c> id the call carries: the first argument evaluated on its own. It never
        /// references the job instance, so the id is readable without instantiating the job class.
        /// </summary>
        public Guid RenderJobId => Expression.Lambda<Func<Guid>>(Body.Arguments[0]).Compile()();

        /// <summary>Records one call.</summary>
        public static RecordedEnqueue Of<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall)
            where TJob : class, IBackgroundJob =>
            new(typeof(TJob), methodCall);
    }
}
