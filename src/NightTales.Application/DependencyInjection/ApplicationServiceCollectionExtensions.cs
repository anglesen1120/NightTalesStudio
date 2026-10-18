using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Catalog;
using NightTales.Application.Abstractions.Compliance;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Stories;
using NightTales.Application.Features.Account.Legal;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Research;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Application.Features.Research.Sources;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Application.Features.SeriesBible.Jobs;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Moderation;
using NightTales.Application.Features.Stories.Render;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Rendering.Subtitles;

namespace NightTales.Application.DependencyInjection;

/// <summary>
/// The Application layer's own registrations (09 README D4/F03): the ports whose implementation is not
/// written yet, so each composition root has one call for them.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Application-layer defaults the Api and the Worker both need.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 09.4.1-T1: no prompt catalog is shipped yet (02.1.4-T1 loads the embedded <c>prompts/</c>
    /// manifest), so every preset key is unknown and a series may only carry a null <c>storyPreset</c>
    /// ([DECISION] 09.4.1 plan §4 U1). <c>TryAdd</c> keeps the replacement additive: 02.1.4-T1 registers
    /// its own <see cref="IStoryPresetCatalog"/> and wins.
    /// </para>
    /// <para>
    /// The handlers and validators themselves need no call here: the Mediator source generator and
    /// <c>AddValidatorsFromAssemblyContaining</c> register them from the Application assembly.
    /// </para>
    /// </remarks>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightTalesApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IStoryPresetCatalog, NullStoryPresetCatalog>();

        // 02.2.3-T2: stateless and pure; needs the IPromptCatalog singleton that both hosts register through AddPromptCatalog.
        services.TryAddSingleton<IPromptAssembler, PromptAssembler>();

        // 02.5.5-T1 (02 CR-24): the real guard, in place of 02.2.2-T2's fail-closed default. Scoped, like both
        // consumers (the generation pipeline and the approval gate) and like the IReadDb it reads through, whose
        // tenant filter is then the caller's: a job reads exactly what its user's request would have read.
        services.TryAddScoped<IFactSheetGuard, FactSheetGuard>();

        services.TryAddScoped<LegalAcceptanceReader>();
        services.TryAddScoped<ILegalAcceptance, LegalAcceptanceService>();

        // 02.3.3-T1: the real approval gate (sub-checks 3a-3e) plus the ta.v1 attestation text reader. The
        // gate reads the read model, the fact-sheet guard, the corpus provider and the bound options; the
        // provider resolves the ta.v1 text through the D5 prompt catalog both hosts register.
        services.TryAddScoped<IStoryApprovalGate, StoryApprovalGate>();
        services.TryAddSingleton<IAttestationTextProvider, AttestationTextProvider>();
        services.TryAddScoped<IStoryRenderInputProvider, StoryRenderInputProvider>();
        services.TryAddScoped<StoryDraftModeration>();

        // 02.8.2-T1 (plan §4 UC6): the owner-action notice a gate stop writes. It adds the notice, its audit row
        // and the `autopilot_owner_action_required` e-mail row to the caller's unit of work — the gate evaluator's,
        // in the Worker — and the remaining gate services of this feature are registered by 02.8.2-T1's own
        // composition lines beside it.
        services.TryAddScoped<AutopilotNotificationService>();

        // 02.8.2-T1 (plan §4): the gate engine itself — the two evaluators, the gate_results writer, the outcome
        // recorder, the approval service and the instruments. `Autopilot:Gates` is bound by each host through
        // AddAutopilotGatesOptions (Application cannot reference the options binder).
        services.AddAutopilotGates();

        // 02.6.3-T1: the series bible's instruments; its two handlers inject them in both hosts.
        services.TryAddSingleton<SeriesBibleMetrics>();

        // 02.6.3-T2: the one write path an accepted proposal takes — the manual accept command and the proposal
        // job's autopilot auto-accept both go through it, so neither can grow a second way of applying ops.
        // Scoped: it writes through the caller's tracked context, inside the caller's row lock and transaction.
        services.TryAddScoped<IBibleProposalApplier, BibleProposalApplier>();

        // 02.7.2-T2 (R7): the one write path of a url source — the episode lock, the state gate, the URL and cap
        // checks and the inserts — so POST /sources and the candidate add command (which must not send a nested
        // AddSourceCommand) cannot drift apart. Scoped: it writes through the caller's tracked context, inside the
        // caller's row lock and transaction.
        services.TryAddScoped<ISourceAdder, SourceAdder>();

        // 05.1.2-T1 / 09.6.1-T1 (05 D6/D7): the subtitle-presets catalog embedded in the Domain assembly,
        // read once. The factory is lazy: it runs on the first resolution (the first RenderEpisode handler
        // construction), not at composition-root build time. A throwing factory caches nothing, so the next
        // resolution runs it again. A build missing the resource or carrying an invalid document fails there with
        // a descriptive exception (the catalog is a deployment precondition, never a per-request null).
        services.TryAddSingleton(provider =>
            SubtitlePresetCatalog.LoadEmbedded().Match(
                catalog => catalog,
                errors => throw new InvalidOperationException(
                    $"The embedded subtitle-presets catalog '{SubtitlePresetCatalog.ResourceName}' is invalid: "
                    + string.Join("; ", errors.Select(error => $"{error.Code}: {error.Description}")))));

        return services;
    }

    /// <summary>
    /// Registers the story generation job's services (02.2.2-T3): the pipeline, the progress writer, the request factory and
    /// the metrics. The Worker calls it; the options (<c>Story:Job</c>, <c>Story:Sweeper</c>, <c>Story:Review</c>) are bound
    /// in the Worker's composition root.
    /// </summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddStoryGeneration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<StoryGenerationMetrics>();
        services.TryAddScoped<StoryWriteRequestFactory>();
        services.TryAddScoped<StoryGenerationProgress>();
        services.TryAddScoped<StoryGenerationPipeline>();

        return services;
    }

    /// <summary>
    /// Registers the fact-sheet generation job's services (02.5.3-T2, plan §7): the pipeline that claims a queued
    /// <c>fact_sheet</c> run, extracts and validates its facts and saves the draft version. The Worker calls it; its
    /// options (<c>FactSheet:Job</c>, and the job's own copy of the <c>Facts</c> budget) are bound in the Worker's
    /// composition root, and the run's cost estimator (<c>IFactSheetCostEstimator</c>, the command's too) lives with
    /// the usage metering both hosts compose, next to <c>IStoryCostEstimator</c>.
    /// </summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddFactSheetGeneration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<GenerateFactSheetPipeline>();

        return services;
    }

    /// <summary>
    /// Registers the series-bible proposal job's pipeline (02.6.3-T2, plan §7.1): the handler
    /// <see cref="ProposeBibleUpdateHandler"/> resolves in its own scope, once the job has pinned the run's owner.
    /// The Worker calls it, next to the outbox consumer that queues the run.
    /// </summary>
    /// <remarks>
    /// Only the handler is registered here: every collaborator is owned by the layer that owns it — the generator
    /// with the text client, the applier with the rest of this layer (the Api's accept route resolves it too, and
    /// this method is Worker-only because the caps below are bound there), <c>Bible:Limits</c> and the audit,
    /// ledger, price, prompt and moderation ports by persistence or the usage metering both hosts compose, and the
    /// four op caps (<c>BibleProposalCaps</c>, bound from <c>Bible:Proposal</c>) by this root.
    /// </remarks>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddBibleProposalGeneration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ProposeBibleUpdateHandler>();

        return services;
    }

    /// <summary>
    /// Registers the idea step of one autopilot slot (02.8.1-T1, plan §4 U1): the planner, the two checkers of
    /// its 02.8.1-T2 steps, and the instruments of its batch. The Worker calls it, next to the job that runs a tick.
    /// </summary>
    /// <remarks>
    /// Worker-only, like <see cref="AddBibleProposalGeneration"/>: the planner's one paid call goes through
    /// <c>IIdeaGenerator</c> and the novelty checker's through <c>IIdeaNoveltyJudge</c>, both composed with the
    /// OpenAI text adapter (<c>AddOpenAiTextClient</c>), and the Api has no text client. Everything else the three
    /// take is registered by persistence (<c>Autopilot:Ideas</c>: the count, the ceiling, <c>IdeaLimits</c> and the
    /// three 02.8.1-T2 sections, as their plain values) or by the usage metering both hosts compose, so this method
    /// adds only the types of this feature.
    /// </remarks>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddIdeaGeneration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IdeaMetrics>();
        services.TryAddScoped<IIdeaPlanner, IdeaPlanner>();

        // 02.8.1-T2: the planner's two steps of the same paid tick. They are registered here, not with the
        // scoped readers of `AddPersistence`, for the same reason as the planner: the novelty checker takes the
        // Worker-only `IIdeaNoveltyJudge` and both take the instrument above, which the Api host has neither of.
        services.TryAddScoped<IIdeaNoveltyChecker, IdeaNoveltyChecker>();
        services.TryAddScoped<IIdeaPolicyChecker, IdeaPolicyChecker>();

        return services;
    }
}
