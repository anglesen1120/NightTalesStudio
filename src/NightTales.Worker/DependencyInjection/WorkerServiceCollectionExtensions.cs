using System.Reflection;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.DependencyInjection;
using NightTales.Application.Features.Assets.Jobs;
using NightTales.Application.Features.Episodes.Outbox;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Application.Features.Research.Outbox;
using NightTales.Application.Features.Research.Sources.Outbox;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Application.Features.SeriesBible.Events;
using NightTales.Application.Features.Stories.Jobs;
using NightTales.Application.Features.Stories.Outbox;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Rendering;
using NightTales.Application.Telemetry;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes.Events;
using NightTales.Domain.Platform.Events;
using NightTales.Domain.Research.Events;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Events;
using NightTales.Domain.Stories.Generation.Events;
using NightTales.Infrastructure.Compliance.Deletion;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Email;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.OpenAI;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Flags;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.Infrastructure.Publishing;
using NightTales.Infrastructure.Rendering;
using NightTales.Infrastructure.Security;
using NightTales.Infrastructure.Storage;
using NightTales.Worker.Health;
using NightTales.Worker.Rendering;
using NightTales.Worker.Services;

namespace NightTales.Worker.DependencyInjection;

/// <summary>
/// The one Worker composition root (09 README D13, 09 W3 CR-42): <c>Worker/Program.cs</c> and the
/// test harness's <c>WorkerHost</c> both build the service collection from here, so the harness runs
/// exactly the production graph — persistence, Mediator, jobs, the recurring scan and the health
/// checks — and only the Hangfire server is added separately (production always, tests in real-server
/// mode only).
/// </summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// The factory of the hosted descriptor that runs the outbox loop (09.2.4-T1): it resolves the
    /// dispatcher singleton, so the loop and <c>WorkerHost.RunOutboxOnceAsync</c> drive one instance.
    /// </summary>
    /// <remarks>
    /// A named field rather than a lambda so <see cref="WithoutOutboxDispatcherLoop"/> can find exactly this
    /// descriptor by reference — the compiler-generated lambda of a method body is not addressable.
    /// </remarks>
    public static readonly Func<IServiceProvider, IHostedService> OutboxDispatcherLoopFactory =
        provider => provider.GetRequiredService<OutboxDispatcher>();

    /// <summary>
    /// The factory of the hosted descriptor that runs the health poller (11.3.3-T1, 11 README D13): it
    /// resolves the poller singleton, so a test drives the very instance the loop owns through
    /// <c>NodeHealthPoller.PollOnceAsync</c>.
    /// </summary>
    /// <remarks>
    /// A named field for the same reason as <see cref="OutboxDispatcherLoopFactory"/>:
    /// <see cref="WithoutNodeHealthPoller"/> finds exactly this descriptor by reference.
    /// </remarks>
    public static readonly Func<IServiceProvider, IHostedService> NodeHealthPollerFactory =
        provider => provider.GetRequiredService<NodeHealthPoller>();

    /// <summary>
    /// The factory of the hosted descriptor that runs the render poller (09.6.3-T1, 09 README BD7): it
    /// resolves the poller singleton, so a test drives the very instance the loop owns through
    /// <c>RenderPollingService.TickAsync</c>.
    /// </summary>
    /// <remarks>
    /// A named field for the same reason as <see cref="OutboxDispatcherLoopFactory"/>:
    /// <see cref="WithoutRenderPollingService"/> finds exactly this descriptor by reference.
    /// </remarks>
    public static readonly Func<IServiceProvider, IHostedService> RenderPollingServiceFactory =
        provider => provider.GetRequiredService<RenderPollingService>();

    /// <summary>Registers everything the Worker needs except the Hangfire server.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="assemblies">
    /// Extra assemblies to scan for job classes and recurring registrations, on top of this assembly
    /// and the Application assembly (the harness passes the assembly that declares its probe jobs).
    /// </param>
    public static IServiceCollection AddWorkerServices(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assemblies);

        // 02.2.3-T1 (02 README D5/F22): the Worker writes stories, so it loads and validates the prompt
        // catalog at start-up like the Api. Registered before the rest of the graph so its gate is the
        // first hosted service to start: a catalog that does not match its manifest must fail the boot
        // before anything else does work.
        services.AddPromptCatalog();

        services.AddOptions<WorkerOptions>()
            .BindConfiguration(WorkerOptions.SectionName)
            .Validate(
                options => options.HealthPort is > 0 and <= 65535,
                $"'{WorkerOptions.SectionName}:{nameof(WorkerOptions.HealthPort)}' must be a TCP port between 1 and 65535.")
            .ValidateOnStart();

        services.AddPersistence(configuration, assemblies);

        // 02.2.4-T1 (02 README D14): the Worker meters every provider call it makes.
        services.AddUsageMetering();

        // 02.2.1-T2 (02 README D7/D21): the Worker writes stories — the OpenAI text adapter, the IStoryWriter
        // over it, and the Application defaults (the IPromptAssembler it assembles with). Needs OpenAi:ApiKey.
        services.AddNightTalesApplication();
        services.AddOpenAiTextClient();

        // 09.9.2-T2 (09 README BD13): the same feature-flag read service the Api composes — the cached port
        // a job or outbox handler checks, the writer's NOTIFY notifier, and this instance's
        // `LISTEN feature_flags_changed` invalidator, so a flip reached on one host is believed on all.
        services.AddFeatureFlags(configuration);

        // 09.9.4-T1 (09 README BD12/WF15): the Worker protects and reads back values with the same ring
        // and the same DP_KEK as the Api (11 §10:284), so a token the Api protected — a channel
        // connection, a settings secret — is readable here, and each purpose stays isolated.
        services.AddNightTalesDataProtection(configuration);

        // 06.1.2-T2: the `channel_connections` store, its row lock and the refresh-under-lock token path
        // (`channel_connection_changed.v1` itself needs no registration here: the outbox catalogue is the
        // `[OutboxEvent]` scan of `OutboxEventRegistry`, and nothing in this wave consumes the event yet).
        // Only the store is registered: `YouTube:Clients` is an Api-only section, so the OAuth client graph
        // arrives with the upload wiring of 06.2.3, which is the first job to resolve `ITokenService`.
        services.AddChannelConnections();

        // 09.2.3-T1 (09 README D10): the Worker's actor is the scoped system context. Every user-owned
        // row stays invisible until a handler calls ISystemContext.For(userId), and both interfaces
        // resolve to the same instance so the filter and the ordering guard share one state.
        services.RemoveAll<ICurrentUser>();
        services.RemoveAll<ISystemContext>();
        services.AddScoped<ScopedCurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<ScopedCurrentUser>());
        services.AddScoped<ISystemContext>(provider => provider.GetRequiredService<ScopedCurrentUser>());

        services.AddNightTalesMediator();
        services.AddJobs(configuration, [typeof(WorkerServiceCollectionExtensions).Assembly, .. assemblies]);

        // 05.5.1-T2 (05 README D10): the render-quality thresholds the ingest job applies to a measured
        // `render-check.v1` are configuration, because 05.2.1-T1/05.5.2-T1 calibrate them; they are
        // validated on start so an impossible policy fails the boot, not the first render.
        services.AddRenderQuality(configuration);

        // 09.4.2-T1 (plan §3 step 6): the Worker composes the same public catalog URL as the Api, so a
        // template it renders or mails never disagrees with the Api about what a storage key points at.
        services.AddPublicAssetUrl();

        // 09.2.6-T3 (09 README BD9): the e-mail pipeline — the validated template set, the renderer, the
        // SMTP adapter and the `email_requested.v1` consumer that enqueues SendEmailJob. The Api registers
        // the same graph (it is the producer side); the Worker is the host that actually sends.
        services.AddEmail(configuration);

        // 09.5.1-T1 (09 README D11/D16, F14): the one consumer of `episode_cancelled.v1`. It runs in the
        // delivery's own scope — already pinned to the event's user — and dispatches a command through
        // ISender, so the D4 pipeline and its transaction own the asset update. It lives in the
        // composition root and not in AddPersistence because a Worker-only consumer has no business in
        // the Api host's outbox path (the Api only writes outbox rows, 09 README D12).
        services.AddOutboxHandler<EpisodeCancelled, EpisodeCancelledAssetRetentionHandler>();

        // 02.5.1-T1 (plan §5.5): `source_removed.v1` becomes an `EpisodeUpdated` NOTIFY, ids only. The Api only writes
        // the outbox row; the Worker delivers it.
        services.AddOutboxHandler<SourceRemoved, SourceRemovedRealtimeHandler>();

        // 02.5.2-T2 (plan §5.5): `source_fetched.v1`, raised by the Api's result route, becomes the same `EpisodeUpdated`
        // NOTIFY, ids only, so the sources list shows the fetched or failed source.
        services.AddOutboxHandler<SourceFetched, SourceFetchedRealtimeHandler>();

        // 02.3.1-T1 (plan §Design step 7, 02 CR-7): `story_version_saved.v1` becomes an `EpisodeUpdated` NOTIFY, ids only,
        // so the editor and the episode page refetch after an edit.
        services.AddOutboxHandler<StoryVersionSaved, StoryVersionSavedRealtimeHandler>();

        // 02.3.2-T1: checkpoint transitions invalidate the owner's episode projection, ids only.
        services.AddOutboxHandler<StoryApproved, StoryApprovedRealtimeHandler>();
        services.AddOutboxHandler<StoryReopened, StoryReopenedRealtimeHandler>();

        // 02.2.2-T3 (02 README D21): the story generation job, its outbox consumers, the stage progress and the sweeper.
        // Every key has a default, so none is a required start-up key; a value out of range fails the boot.
        services.AddStoryGeneration();
        services.AddStoryQuality();
        services.AddSimilarity();
        services.AddOptions<StoryJobOptions>()
            .BindConfiguration(StoryJobOptions.SectionName)
            .Validate(
                options => StoryJobOptions.Validate(options).Count == 0,
                $"'{StoryJobOptions.SectionName}' timeouts must be between {StoryJobOptions.MinTimeoutSeconds} and {StoryJobOptions.MaxTimeoutSeconds} seconds, and TimeoutSecondsLong must not be below TimeoutSeconds.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<StoryJobOptions>>().Value);
        services.AddOptions<StorySweeperOptions>()
            .BindConfiguration(StorySweeperOptions.SectionName)
            .Validate(
                options => options.BatchSize is >= 1 and <= StorySweeperOptions.MaxBatchSize,
                $"'{StorySweeperOptions.SectionName}:{nameof(StorySweeperOptions.BatchSize)}' must be between 1 and {StorySweeperOptions.MaxBatchSize}.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<StorySweeperOptions>>().Value);
        services.AddOptions<StoryReviewOptions>().BindConfiguration(StoryReviewOptions.SectionName).ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<StoryReviewOptions>>().Value);

        // 02.6.2-T1 (02 README D21): the single-revision switch. Every key has a default; a MaxRevisions other
        // than 0 or 1, or a MinTextSimilarity outside (0, 1], fails the boot. Bound as the value object so the
        // pipeline injects it the same way it injects StoryReviewOptions.
        services.AddOptions<StoryRevisionOptions>()
            .BindConfiguration(StoryRevisionOptions.SectionName)
            .Validate(
                options => StoryRevisionOptions.Validate(options).Count == 0,
                $"'{StoryRevisionOptions.SectionName}:{nameof(StoryRevisionOptions.MaxRevisions)}' must be 0 or {StoryRevisionOptions.MaxRevisionsCeiling}, and '{nameof(StoryRevisionOptions.MinTextSimilarity)}' must be greater than 0 and at most 1.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<StoryRevisionOptions>>().Value);

        services.AddOutboxHandler<StoryGenerationRequested, GenerateStoryJobHandler>();
        services.AddOutboxHandler<StoryGenerationStageChanged, StoryGenerationStageChangedRealtimeHandler>();
        services.AddOutboxHandler<StoryGenerated, StoryGeneratedRealtimeHandler>();
        services.AddOutboxHandler<StoryGenerationFailed, StoryGenerationFailedRealtimeHandler>();

        // 02.5.3-T2 (02 README D21, 02.5.3 plan §7): the fact-sheet generation job, the consumer of the
        // `fact_sheet_generation_requested.v1` row the Api writes, and the two realtime consumers of its outcome. Every
        // key of `FactSheet:Job` has a default, so none is a required start-up key; a value out of range fails the boot.
        // The `Facts` budget is bound here for the pipeline and again by the OpenAI text client this root composes above
        // (the adapter validates its own copy), and `IFactSheetCostEstimator` comes from the usage metering both hosts
        // compose.
        services.AddFactSheetGeneration();
        services.AddOptions<FactSheetJobOptions>()
            .BindConfiguration(FactSheetJobOptions.SectionName)
            .Validate(
                options => FactSheetJobOptions.Validate(options).Count == 0,
                $"'{FactSheetJobOptions.SectionName}:{nameof(FactSheetJobOptions.TimeoutSeconds)}' must be between {FactSheetJobOptions.MinTimeoutSeconds} and {FactSheetJobOptions.MaxTimeoutSeconds} seconds.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<FactSheetJobOptions>>().Value);

        // The pipeline reads the same input budget the OpenAI extractor validates its request against, through its own
        // options class because the Application layer cannot reference the Infrastructure one that binds the section for
        // the adapter. Both are validated on start and read the same keys.
        services.AddOptions<FactSheetExtractionOptions>()
            .BindConfiguration(FactSheetExtractionOptions.SectionName)
            .Validate(
                options => FactSheetExtractionOptions.Validate(options).Count == 0,
                $"'{FactSheetExtractionOptions.SectionName}' must hold the fact-extraction budget keys with values in range.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<FactSheetExtractionOptions>>().Value);

        services.AddOutboxHandler<FactSheetGenerationRequested, GenerateFactSheetJobHandler>();
        services.AddOutboxHandler<FactSheetGenerated, FactSheetGeneratedRealtimeHandler>();
        services.AddOutboxHandler<FactSheetGenerationFailed, FactSheetGenerationFailedRealtimeHandler>();
        services.AddOutboxHandler<FactSheetSaved, FactSheetSavedRealtimeHandler>();
        services.AddOutboxHandler<FactSheetApproved, FactSheetApprovedRealtimeHandler>();

        // 02.6.3-T2 (02 README D21, 02.6.3 plan §7.1): the bible-proposal consumer of `story_approved.v1`. The
        // approval's `ApprovalSource` picks the branch — a user approval stages the ops as the series' pending
        // proposal, an autopilot fiction approval applies them at once (F27, with a system-actor audit row) — and
        // the paid call behind it is `ProposeBibleUpdateJob`, which the background-job scan of this root already
        // registered from the Application assembly, with the deferred `BibleProposalHandoffJob` the handler
        // schedules when another proposal of the same series is still open (one paid call per series at a time).
        services.AddOutboxHandler<StoryApproved, StoryApprovedBibleProposalHandler>();

        // The job's pipeline: `ProposeBibleUpdateJob` resolves the handler in its own scope, after it has pinned the
        // run's owner — so the Application layer registers it beside the outbox consumer that queues the run. The
        // Api never resolves it (its accept route takes the applier, which both roots get from
        // `AddNightTalesApplication`), and the caps the handler takes are the `BibleProposalJobOptions` bound below.
        services.AddBibleProposalGeneration();

        // 02.8.1-T1: the idea step of one autopilot slot (`IdeaPlanner`, plan §4 U1), which 02.8.3-T1's tick calls
        // next to `MarkUsedAsync`. Worker-only like the proposal pipeline above, because its one paid call goes
        // through `IIdeaGenerator`, composed with the OpenAI text adapter this root registers; the count, the
        // ceiling and `IdeaLimits` come from `Autopilot:Ideas`, bound by persistence for both hosts.
        services.AddIdeaGeneration();

        // `Bible:Proposal` is read by both halves of this feature: the handler reads the kill switch through
        // `IBibleProposalSwitch`, whose Worker implementation holds the `IOptionsMonitor` so the switch takes
        // effect without a restart, and the job takes the four batch caps. This root binds the Application
        // options class, because the Application layer cannot reference the Infrastructure class that binds the
        // same section for the text adapter (nor `Microsoft.Extensions.Options` itself, which the architecture
        // rule keeps out of that assembly). Every key has a default, so none is a required start-up key; a
        // negative cap fails the boot.
        services.AddOptions<BibleProposalJobOptions>()
            .BindConfiguration(BibleProposalJobOptions.SectionName)
            .Validate(
                options => options.MaxOps >= 0
                    && options.MaxCharacterOps >= 0
                    && options.MaxPlaceOps >= 0
                    && options.MaxRuleOps >= 0,
                $"'{BibleProposalJobOptions.SectionName}' must hold non-negative op caps.")
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<BibleProposalJobOptions>>().Value);

        // The handler's view of that section: the switch is read live, so the rollback in the plan's §11 needs no
        // restart, while the four caps below are the snapshot the job takes once.
        services.TryAddSingleton<IBibleProposalSwitch, BibleProposalSwitch>();

        // `BibleProposalCaps` is that value's four caps as the Domain record the post-filter and the merge guard
        // take, so the Application layer never references the Infrastructure options class that binds the section.
        services.TryAddSingleton(provider =>
        {
            var proposal = provider.GetRequiredService<IOptions<BibleProposalJobOptions>>().Value;

            return new BibleProposalCaps(
                MaxOps: proposal.MaxOps,
                MaxCharacterOps: proposal.MaxCharacterOps,
                MaxPlaceOps: proposal.MaxPlaceOps,
                MaxRuleOps: proposal.MaxRuleOps);
        });

        // 11.3.3-T1 (11 README D13/F09–F13): the render-node registry, the pinned-TLS connection factory,
        // the health cache, the drift/up instruments and the config seeder. The optional `RenderNodes`
        // section is validated on start, so an impossible cadence or hysteresis fails the boot.
        services.AddRenderNodes(configuration);

        // 09.6.2-T1 (09 README BD6, WF08/W9): the two render-node pipelines and the clients over the same
        // connection factory. Submit/status/cancel run on the retrying pipeline, the DELETE on the no-retry
        // one, and the `Render` section is optional: every key carries the BD6 default.
        services.AddRenderClients(configuration);

        // The poller is a BackgroundService, not a Hangfire recurring job: a sub-minute cron is not
        // reliable (F10/P9, 09 README D11). It is registered as a singleton and hosted through that
        // instance, so a host and the harness resolve the very poller the loop owns.
        services.AddSingleton<NodeHealthPoller>();
        services.AddSingleton<IHostedService>(NodeHealthPollerFactory);

        // 09.9.2-T2 (09 README BD13): the `feature_flags_changed.v1` consumer — it drops the key from this
        // Worker instance's flag cache. It is idempotent and writes nothing: the writer owns the row, the
        // audit row and the NOTIFY; this handler is the outbox half of the two eviction paths.
        services.AddOutboxHandler<FeatureFlagsChanged, FeatureFlagsChangedHandler>();

        // 09.2.4-T1 appends the hosted OutboxDispatcher inside this composition root (09 W3 CR-42).
        services.AddOptions<OutboxDispatcherOptions>()
            .BindConfiguration(OutboxDispatcherOptions.SectionName)
            .Validate(
                options => options.PollIntervalSeconds > 0,
                $"'{OutboxDispatcherOptions.SectionName}:{nameof(OutboxDispatcherOptions.PollIntervalSeconds)}' must be greater than zero.")
            .Validate(
                options => options.BatchSize > 0,
                $"'{OutboxDispatcherOptions.SectionName}:{nameof(OutboxDispatcherOptions.BatchSize)}' must be greater than zero.")
            .Validate(
                options => options.LeaseMinutes > 0,
                $"'{OutboxDispatcherOptions.SectionName}:{nameof(OutboxDispatcherOptions.LeaseMinutes)}' must be greater than zero.")
            .Validate(
                options => options.MaxAttempts > 0,
                $"'{OutboxDispatcherOptions.SectionName}:{nameof(OutboxDispatcherOptions.MaxAttempts)}' must be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton<OutboxMetrics>();

        // Registered as a singleton and hosted through that instance, so a host and the harness resolve the
        // very dispatcher the Worker's loop owns (WorkerHost.RunOutboxOnceAsync, 09 README D13).
        services.AddSingleton<OutboxDispatcher>();
        services.AddSingleton<IHostedService>(OutboxDispatcherLoopFactory);

        // 09.7.2-T1 (09 README BD10/BD12): the blob store, its health cache and the prober. The sweep
        // (09.7.3-T1), the render ingest (09.6.3-T2) and 02's fetcher path all call it here; the
        // readiness check below reads only the cache the prober fills (BD16).
        services.AddStorage(configuration);

        // 13.3.1-T1 (13 README D4): the deletion engine — hourly job, scope handlers, the K21 start-up
        // validator and the restore pair. It needs the blob store registered above.
        services.AddDeletionEngine(configuration);
        // 09.7.3-T1: the retention sweep's bounds and instruments. Every key has a default, so none is a
        // required start-up key (no placeholder in the boot-context blocks); the ports are persistence's.
        services.AddOptions<RetentionSweepOptions>()
            .BindConfiguration(RetentionSweepOptions.SectionName)
            .Validate(
                options => options.BatchSize is >= 1 and <= RetentionSweepOptions.MaxBatchSize,
                $"'{RetentionSweepOptions.SectionName}:{nameof(RetentionSweepOptions.BatchSize)}' must be between 1 and {RetentionSweepOptions.MaxBatchSize}.")
            .Validate(
                options => options.MaxBatches is >= 1 and <= RetentionSweepOptions.MaxBatchesLimit,
                $"'{RetentionSweepOptions.SectionName}:{nameof(RetentionSweepOptions.MaxBatches)}' must be between 1 and {RetentionSweepOptions.MaxBatchesLimit}.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<RetentionSweepOptions>>().Value);
        services.AddSingleton<RetentionSweepMetrics>();

        // 02.5.2-T2 (02 D10): the fetch-lease sweeper's three keys of the `Fetcher` section. Every key has a default, so none
        // is a required start-up key; a non-positive value fails the boot. The Api binds the same section into its own type.
        services.AddOptions<FetchLeaseSweeperOptions>()
            .BindConfiguration(FetchLeaseSweeperOptions.SectionName)
            .Validate(
                options => FetchLeaseSweeperOptions.Validate(options).Count == 0,
                $"'{FetchLeaseSweeperOptions.SectionName}:{nameof(FetchLeaseSweeperOptions.MaxAttempts)}', '{nameof(FetchLeaseSweeperOptions.RequeueBackoffSeconds)}' and '{nameof(FetchLeaseSweeperOptions.SweepIntervalSeconds)}' must each be at least 1.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<FetchLeaseSweeperOptions>>().Value);
        services.TryAddSingleton<FetchLeaseGauge>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database")
            .AddCheck<HangfireStorageHealthCheck>("hangfire")
            .AddCheck<StorageHealthCheck>(StorageHealthCheck.Name)
            .AddCheck<NodeRegistryHealthCheck>(NodeRegistryHealthCheck.Name);

        // 09.9.3-T1 (09 README BD12): the `render_jobs_active` gauge. The instruments are registered by
        // AddNtsTelemetry in the real host; TryAdd keeps the harness graph, which does not call it, complete.
        var metricsConnectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(metricsConnectionString))
        {
            throw new OptionsValidationException(
                nameof(PersistenceOptions),
                typeof(PersistenceOptions),
                [$"Configuration value 'ConnectionStrings:{PersistenceOptions.ConnectionStringName}' is required for the render-metrics refresher."]);
        }

        services.TryAddSingleton<NightTalesMetrics>();
        services.AddSingleton(new RenderMetricsQuery(metricsConnectionString));
        services.AddSingleton<RenderMetricsRefresher>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<RenderMetricsRefresher>());

        // 09.6.3-T1 (09 README BD7, WF07/W12): the render submit/poll graph the Worker runs — the jobs, the
        // retry policy, the failure recorder, the render instruments and the `render_requested.v1` consumer
        // that queues SubmitRenderJob. It is composed only here: the Api never schedules jobs (D12).
        services.AddRenderJobs();

        // The `Render` cadence and bounds of that graph. Every key has a default; an out-of-range value fails
        // the boot (the checks live in Application as `ImagesOptions`'s do), and the bound value is exposed as
        // the snapshot the poller and the jobs take.
        services.AddOptions<RenderOptions>()
            .BindConfiguration(RenderOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RenderOptions>, RenderOptionsValidation>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<RenderOptions>>().Value);

        // `SubmitRenderJob` reschedules a render after `RenderNodes:HealthPollSeconds` when no node is
        // dispatchable, so the bound value of that section is exposed the same way.
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<RenderNodesOptions>>().Value);

        // 09.6.3-T1 (BD7, WF07/W12): the poller is a BackgroundService, not a Hangfire recurring job — a
        // sub-minute cron is not reliable. It is a singleton hosted through that instance, so a host and the
        // harness resolve the very poller the loop owns; its advisory-lock connection opens on the same
        // platform database as the node poller's guard (`NodeHealthPollerLock`).
        services.AddSingleton(provider => new RenderPollingService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<RenderOptions>>(),
            provider.GetRequiredService<IClock>(),
            metricsConnectionString,
            provider.GetRequiredService<ILogger<RenderPollingService>>()));
        services.AddSingleton<IHostedService>(RenderPollingServiceFactory);

        return services;
    }

    /// <summary>
    /// Drops the hosted outbox loop from <paramref name="services"/> and leaves the dispatcher singleton in
    /// place (09 README D13): the test harness drives delivery through <c>WorkerHost.RunOutboxOnceAsync</c>,
    /// so the loop must never start.
    /// </summary>
    /// <remarks>
    /// The descriptor has to go before the host is built. Stopping the loop after the start is not enough:
    /// <c>BackgroundService.StartAsync</c> schedules the loop, so it can claim and deliver a row before any
    /// stop reaches it. Removing it here leaves no window at all.
    /// </remarks>
    /// <param name="services">The service collection of the host.</param>
    public static IServiceCollection WithoutOutboxDispatcherLoop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var loop = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && ReferenceEquals(descriptor.ImplementationFactory, OutboxDispatcherLoopFactory));

        if (loop is not null)
        {
            services.Remove(loop);
        }

        return services;
    }

    /// <summary>
    /// Drops the hosted health poller from <paramref name="services"/> and leaves the poller singleton in
    /// place (11.3.3-T1): the test harness drives one tick through <c>NodeHealthPoller.PollOnceAsync</c>, so
    /// the 30-second loop must never start and no test depends on a timer.
    /// </summary>
    /// <remarks>
    /// The same reason as <see cref="WithoutOutboxDispatcherLoop"/>: the descriptor has to go before the host
    /// is built, because <c>BackgroundService.StartAsync</c> schedules the loop and its first tick would run
    /// against a clock no test controls.
    /// </remarks>
    /// <param name="services">The service collection of the host.</param>
    public static IServiceCollection WithoutNodeHealthPoller(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var poller = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && ReferenceEquals(descriptor.ImplementationFactory, NodeHealthPollerFactory));

        if (poller is not null)
        {
            services.Remove(poller);
        }

        return services;
    }

    /// <summary>
    /// Drops the hosted render poller from <paramref name="services"/> and leaves the poller singleton in
    /// place (09.6.3-T1): the harness drives one tick through <c>RenderPollingService.TickAsync</c>, so the
    /// <c>Render:PollSeconds</c> loop must never start — a started loop would claim real jobs and call the
    /// adapter against a clock no test controls.
    /// </summary>
    /// <remarks>
    /// The same reason as <see cref="WithoutOutboxDispatcherLoop"/>: the descriptor has to go before the host
    /// is built, because <c>BackgroundService.StartAsync</c> schedules the loop and its first tick runs
    /// immediately.
    /// </remarks>
    /// <param name="services">The service collection of the host.</param>
    public static IServiceCollection WithoutRenderPollingService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var poller = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && ReferenceEquals(descriptor.ImplementationFactory, RenderPollingServiceFactory));

        if (poller is not null)
        {
            services.Remove(poller);
        }

        return services;
    }

    /// <summary>
    /// Adds the single Hangfire server of the platform (09.2.6 plan §7): one server on every queue,
    /// with the worker counts and poll intervals of the <c>Jobs</c> section. Production calls this from
    /// <c>Program.cs</c>; the test harness calls it only in real-server mode (D13).
    /// </summary>
    public static IServiceCollection AddWorkerHangfireServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddHangfireServer((provider, options) =>
        {
            var jobs = provider.GetRequiredService<IOptions<JobsOptions>>().Value;

            options.Queues = [.. JobQueues.All];
            options.WorkerCount = jobs.TotalWorkerCount;
            options.SchedulePollingInterval = TimeSpan.FromSeconds(jobs.SchedulePollSeconds);
        });
    }
}

/// <summary>Fails the start-up on an invalid <c>Render</c> section (09.6.3-T1), with every problem listed.</summary>
/// <remarks>
/// The checks live in Application (<see cref="RenderOptionsValidator"/>, the <c>ImagesOptionsValidator</c>
/// precedent) because that assembly cannot reference <c>Microsoft.Extensions.Options</c>; this is the
/// <see cref="IValidateOptions{T}"/> adapter of the Worker's composition root.
/// </remarks>
internal sealed class RenderOptionsValidation : IValidateOptions<RenderOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RenderOptions options)
    {
        var problems = RenderOptionsValidator.Validate(options);

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems.Select(problem => $"{RenderOptions.SectionName}: {problem}"));
    }
}
