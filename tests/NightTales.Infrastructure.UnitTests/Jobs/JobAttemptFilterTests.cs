using System.Globalization;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Jobs;

/// <summary>
/// The ambient attempt of 09.6.3-T2 step 5 (WT52): the Infrastructure server filter publishes the
/// attempt Hangfire is running — <c>RetryCount + 1</c> out of the job class's declared retries plus its
/// first run — and clears it again when the attempt ends, so a job body reads its own attempt and the
/// next attempt on the same pooled thread reads the default "one attempt, and it is the last".
/// </summary>
/// <remarks>
/// The two Hangfire contexts are built by hand with <see cref="RetryCountConnection"/> instead of by a
/// server: the filter reads the attempt counter as the <c>RetryCount</c> job parameter and reaches no
/// other member of <see cref="PerformContext"/>, so a connection that answers that one question is
/// enough to run the real filter through both callbacks — the storage is <see langword="null"/>.
/// <para>
/// Every test that performs an attempt also ends it, exactly as a server does. The scope is
/// <c>AsyncLocal</c>-based and <see cref="JobAttemptScope.Set"/> restores whatever the context had
/// before it, so a value left behind by one test would be the "previous" one the next test restores.
/// </para>
/// </remarks>
public sealed class JobAttemptFilterTests
{
    [Fact]
    public void Resolve_counts_the_first_run_as_attempt_one_of_the_declared_retries_plus_one()
    {
        var (attempt, maxAttempts) = JobAttemptFilter.Resolve(retryCount: 0, retries: 5);

        attempt.ShouldBe(1);
        maxAttempts.ShouldBe(6);
    }

    [Fact]
    public void Resolve_makes_the_declared_retry_the_last_attempt()
    {
        var (attempt, maxAttempts) = JobAttemptFilter.Resolve(retryCount: 5, retries: 5);

        attempt.ShouldBe(6);
        maxAttempts.ShouldBe(6);
    }

    [Fact]
    public void Resolve_gives_a_class_without_the_policy_its_single_documented_attempt()
    {
        var (attempt, maxAttempts) = JobAttemptFilter.Resolve(retryCount: 0, retries: 0);

        attempt.ShouldBe(1);
        maxAttempts.ShouldBe(1);
    }

    [Fact]
    public void OnPerforming_publishes_the_attempt_the_retry_count_and_the_declared_policy_agree_on()
    {
        var filter = new JobAttemptFilter();
        var context = NewPerformContext(typeof(RetryingJob), retryCount: 0);

        filter.OnPerforming(new PerformingContext(context));

        JobAttemptScope.Value.ShouldBe((1, 6));
        var attempt = Ambient();
        attempt.Attempt.ShouldBe(1);
        attempt.MaxAttempts.ShouldBe(6);
        attempt.IsLastAttempt.ShouldBeFalse();

        filter.OnPerformed(Performed(context));
    }

    [Fact]
    public void OnPerforming_publishes_the_last_attempt_on_the_declared_final_retry()
    {
        var filter = new JobAttemptFilter();
        var context = NewPerformContext(typeof(RetryingJob), retryCount: 5);

        filter.OnPerforming(new PerformingContext(context));

        JobAttemptScope.Value.ShouldBe((6, 6));
        Ambient().IsLastAttempt.ShouldBeTrue();

        filter.OnPerformed(Performed(context));
    }

    [Fact]
    public void OnPerforming_keeps_a_run_past_the_declared_retries_as_the_last_attempt()
    {
        var filter = new JobAttemptFilter();
        var context = NewPerformContext(typeof(RetryingJob), retryCount: 6);

        filter.OnPerforming(new PerformingContext(context));

        JobAttemptScope.Value.ShouldBe((7, 6));
        Ambient().IsLastAttempt.ShouldBeTrue();

        filter.OnPerformed(Performed(context));
    }

    [Fact]
    public void OnPerforming_gives_a_class_without_the_policy_one_attempt_and_makes_it_the_last()
    {
        var filter = new JobAttemptFilter();
        var context = NewPerformContext(typeof(JobWithoutPolicy), retryCount: 0);

        filter.OnPerforming(new PerformingContext(context));

        JobAttemptScope.Value.ShouldBe((1, 1));
        Ambient().IsLastAttempt.ShouldBeTrue();

        filter.OnPerformed(Performed(context));
    }

    [Fact]
    public void OnPerformed_clears_the_attempt_so_the_context_falls_back_to_one_last_attempt()
    {
        var filter = new JobAttemptFilter();
        var context = NewPerformContext(typeof(RetryingJob), retryCount: 0);

        filter.OnPerforming(new PerformingContext(context));
        filter.OnPerformed(Performed(context));

        JobAttemptScope.Value.ShouldBeNull();
        var attempt = Ambient();
        attempt.Attempt.ShouldBe(1);
        attempt.MaxAttempts.ShouldBe(1);
        attempt.IsLastAttempt.ShouldBeTrue();
    }

    [Fact]
    public void OnPerformed_restores_the_attempt_the_context_had_before_the_job()
    {
        var filter = new JobAttemptFilter();
        var context = NewPerformContext(typeof(RetryingJob), retryCount: 3);

        using var outer = JobAttemptScope.Set(attempt: 9, maxAttempts: 9);

        filter.OnPerforming(new PerformingContext(context));
        JobAttemptScope.Value.ShouldBe((4, 6));

        filter.OnPerformed(Performed(context));
        JobAttemptScope.Value.ShouldBe((9, 9));
    }

    [Fact]
    public void OnPerformed_without_a_perform_leaves_the_context_alone()
    {
        var context = NewPerformContext(typeof(RetryingJob), retryCount: 0);

        new JobAttemptFilter().OnPerformed(Performed(context));

        JobAttemptScope.Value.ShouldBeNull();
    }

    [Fact]
    public void A_second_attempt_after_a_performed_one_does_not_inherit_its_number()
    {
        var filter = new JobAttemptFilter();
        var first = NewPerformContext(typeof(RetryingJob), retryCount: 5);

        filter.OnPerforming(new PerformingContext(first));
        filter.OnPerformed(Performed(first));

        JobAttemptScope.Value.ShouldBeNull();

        var second = NewPerformContext(typeof(RetryingJob), retryCount: 0);
        filter.OnPerforming(new PerformingContext(second));

        JobAttemptScope.Value.ShouldBe((1, 6));
        Ambient().IsLastAttempt.ShouldBeFalse();

        filter.OnPerformed(Performed(second));
    }

    [Fact]
    public void The_host_filter_chain_carries_the_attempt_filter_for_every_job()
    {
        var job = new Job(typeof(RetryingJob), typeof(RetryingJob).GetMethod(nameof(RetryingJob.RunAsync))!);

        new JobAttemptFilterProvider().GetFilters(job)
            .ShouldContain(entry => entry.Instance is JobAttemptFilter);

        JobFilterPipeline.Compose().GetFilters(job)
            .ShouldContain(entry => entry.Instance is JobAttemptFilter);
    }

    /// <summary>The attempt port the ingest resolves, read the way a job body reads it.</summary>
    private static AmbientJobAttempt Ambient() => new();

    /// <summary>The performed callback of <paramref name="context"/>, sharing its item bag with the perform callback.</summary>
    private static PerformedContext Performed(PerformContext context) =>
        new(context, result: null, canceled: false, exception: null);

    /// <summary>
    /// A Hangfire context for <paramref name="jobType"/> whose <c>RetryCount</c> job parameter is
    /// <paramref name="retryCount"/> — the same context a server hands both filter callbacks.
    /// </summary>
    private static PerformContext NewPerformContext(Type jobType, int retryCount)
    {
        var method = jobType.GetMethod(nameof(RetryingJob.RunAsync))!;
        var backgroundJob = new BackgroundJob("probe-job", new Job(jobType, method), DateTime.UtcNow);

        return new PerformContext(
            storage: null,
            connection: new RetryCountConnection(retryCount),
            backgroundJob: backgroundJob,
            cancellationToken: new JobCancellationToken(canceled: false));
    }

    /// <summary>
    /// The connection a hand-built <see cref="PerformContext"/> needs: Hangfire reads a job's attempt
    /// counter as the <c>RetryCount</c> job parameter, so this one answers that question and throws from
    /// every other member — no filter reaches them.
    /// </summary>
    /// <param name="retryCount">The 0-based counter the context reports.</param>
    private sealed class RetryCountConnection(int retryCount) : IStorageConnection
    {
        private const string RetryCountParameter = "RetryCount";

        public string? GetJobParameter(string id, string name) =>
            name == RetryCountParameter ? retryCount.ToString(CultureInfo.InvariantCulture) : null;

        public void Dispose()
        {
        }

        public IWriteOnlyTransaction CreateWriteTransaction() => throw Unsupported();

        public IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => throw Unsupported();

        public string CreateExpiredJob(
            Job job,
            IDictionary<string, string> parameters,
            DateTime createdAt,
            TimeSpan expireIn) => throw Unsupported();

        public IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken) => throw Unsupported();

        public void SetJobParameter(string id, string name, string value) => throw Unsupported();

        public JobData? GetJobData(string jobId) => throw Unsupported();

        public StateData? GetStateData(string jobId) => throw Unsupported();

        public void AnnounceServer(string serverId, ServerContext context) => throw Unsupported();

        public void RemoveServer(string serverId) => throw Unsupported();

        public void Heartbeat(string serverId) => throw Unsupported();

        public int RemoveTimedOutServers(TimeSpan timeOut) => throw Unsupported();

        public HashSet<string> GetAllItemsFromSet(string key) => throw Unsupported();

        public string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) => throw Unsupported();

        public void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs) => throw Unsupported();

        public Dictionary<string, string> GetAllEntriesFromHash(string key) => throw Unsupported();

        /// <summary>The exception every member but the retry-count read ends in.</summary>
        private static NotSupportedException Unsupported() =>
            new("The attempt filter reads only the RetryCount job parameter.");
    }
}

/// <summary>
/// The retrying job class the filter tests read a policy from: <c>Retries = 5</c> like
/// <c>IngestRenderOutputJob</c> (09.6.3-T2 step 1). Only its declaring type and its
/// <see cref="JobPolicyAttribute"/> are ever read — nothing activates or performs it — so
/// <see cref="RunAsync"/> is static and its body is never entered.
/// </summary>
[JobPolicy(Queue = JobQueue.Render, Retries = 5)]
public sealed class RetryingJob
{
    /// <summary>The method the <see cref="Job"/> these tests build names.</summary>
    public static Task RunAsync() => Task.CompletedTask;
}

/// <summary>
/// The job class the filter tests read the platform default from: no <see cref="JobPolicyAttribute"/>, so
/// the documented "queue <c>default</c>, 0 retries" applies and the first attempt is the last one.
/// </summary>
public sealed class JobWithoutPolicy
{
    /// <summary>The method the <see cref="Job"/> these tests build names.</summary>
    public static Task RunAsync() => Task.CompletedTask;
}
