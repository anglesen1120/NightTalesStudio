using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Autopilot.Ideas.Abstractions;

/// <summary>
/// The D5 manifest keys and the wire name of the episode-idea generation step (02.8.1-T1 plan §7.1).
/// </summary>
/// <remarks>
/// One place, because three callers must agree on them: the Infrastructure generator that reads the prompt and
/// the schema out of the prompt catalog, the caller that composes the run's <c>prompt_version</c> (02.8.3-T1),
/// and the tests. The keys are relative to <c>prompts/</c>, as <c>IPromptCatalog</c> requires; <see cref="SchemaFile"/>
/// is the file name the catalog's <c>GetSchema</c> takes, <see cref="SchemaKey"/> the manifest key the composite
/// version names and <see cref="SchemaName"/> the logical name the provider sees (the
/// <c>BibleProposalPrompts</c> shape).
/// </remarks>
public static class IdeaPrompts
{
    /// <summary>The system prompt of the generation step, <c>ideas/generate.v1.md</c>.</summary>
    public const string SystemKey = "ideas/generate.v1.md";

    /// <summary>The manifest key of the strict output schema, <c>schemas/ideas.v1.json</c>.</summary>
    public const string SchemaKey = "schemas/ideas.v1.json";

    /// <summary>The schema's file name under <c>prompts/schemas/</c>, as <c>IPromptCatalog.GetSchema</c> takes it.</summary>
    public const string SchemaFile = "ideas.v1.json";

    /// <summary>The schema's logical name; the adapter maps its dot to the underscore OpenAI allows.</summary>
    public const string SchemaName = "ideas.v1";

    /// <summary>
    /// The provider prompt-cache key (02 README D7/O9): preset-scoped and constant, never per series, per episode
    /// or per slot, so every idea call of every series shares the static system-prompt prefix.
    /// </summary>
    public const string CacheKey = "ideas:generate";
}

/// <summary>
/// The D14 operation every paid call of an ideas run is metered under (02 README D14, 02.8.1-T1).
/// </summary>
/// <remarks>
/// <c>usage_records.operation</c> and the two OpenAI counters of plan §11 (<c>openai_tokens_total</c>,
/// <c>openai_cost_usd_total</c>) all read it, so it is declared once, in Application, where the generator and the
/// planner can both name it (the <c>BibleProposalOperations</c> shape).
/// </remarks>
public static class IdeaOperations
{
    /// <summary>The <c>operation</c> of the one paid generation call of a run.</summary>
    public const string Idea = "idea";
}

/// <summary>
/// The inputs of one episode-idea call (02.8.1-T1 plan §7.1, task Design): the run it is billed to, the series'
/// language, topic, tone and banned themes, the bible, the episode history, the structure the ideas must honour
/// and the research notes the model may build on.
/// </summary>
/// <remarks>
/// <para>
/// The caller (the planner) loads all of it, so the generator reads no database: it serialises the one D6 data
/// object, takes the step prompt and the schema from the prompt catalog and makes the call. Nothing here carries
/// an id of a character, a place, a note or an episode — the model never sees a UUID or a source URL (D6's alias
/// principle, D19) and the run id travels on <see cref="Run"/>, not in the prompt.
/// </para>
/// <para>
/// <see cref="Language"/> and <see cref="Topic"/> are the snake_case spellings the data object's control keys use
/// (<c>vi</c>, <c>true_crime</c>).
/// </para>
/// <para>
/// <see cref="RecentEpisodes"/> is deliberately empty when <see cref="Bible"/> carries summaries: the idea
/// prompt reads <c>bible.episode_summaries</c>, and sending the same text twice would pay for it twice
/// (task file [DECISION]). It is filled from <c>IEpisodeStructureHistory</c> whenever the bible is absent or
/// carries no summary at all — a young series' normal state, whose bible has entities long before its first
/// episode is summarised — so such a series still avoids repeats.
/// </para>
/// <para>
/// <see cref="RequiredTags"/> and <see cref="ForbiddenTags"/> are 02.8.4-T1's steering: the planner fills them
/// from <c>VarietyGuard.AssessBatch</c> over the series' approved-episode history before it makes the call, and
/// passes <see langword="null"/> when the guard constrains nothing, so the data object omits the key entirely.
/// </para>
/// </remarks>
/// <param name="Run">
/// The generation run the call belongs to: its id is the D7 correlation prefix (<c>{generationRunId}:ideas:{attempt}</c>),
/// its attempt is rewritten to 2 on the one invalid-output retry, and its reservation and price version are what
/// the usage row of the call is written against.
/// </param>
/// <param name="Language">The series' content language, snake_case.</param>
/// <param name="Topic">The series' narrative topic, snake_case.</param>
/// <param name="Tone">The series' tone, or <see langword="null"/> when <c>autopilot_settings.tone</c> is unset.</param>
/// <param name="BannedThemes">The series' banned themes; empty when the settings carry none. Never concatenated into the system prompt.</param>
/// <param name="Bible">The bible snapshot as the writer sees it, or <see langword="null"/> when the series has no bible content.</param>
/// <param name="RecentEpisodes">The history titles that stand in for the bible's summaries; empty when the bible carries them.</param>
/// <param name="RequiredTags">The structure values the ideas must use, or <see langword="null"/> when the guard requires none (02.8.4-T1).</param>
/// <param name="ForbiddenTags">The structure values the ideas must not use, or <see langword="null"/> when the guard forbids none (02.8.4-T1).</param>
/// <param name="ResearchNotes">Up to five of the series' own notes (<c>user_note</c> text only, ≤ 400 characters; D19).</param>
/// <param name="Count">How many ideas to ask for, already clamped to <c>1…Autopilot:Ideas:MaxCount</c>.</param>
public sealed record IdeaGenerationRequest(
    RunContext Run,
    string Language,
    string Topic,
    string? Tone,
    IReadOnlyList<string> BannedThemes,
    BibleSnapshot? Bible,
    IReadOnlyList<EpisodeSummaryRef> RecentEpisodes,
    StructureRequirements? RequiredTags,
    StructureForbidden? ForbiddenTags,
    IReadOnlyList<ResearchNoteRef> ResearchNotes,
    int Count);

/// <summary>
/// The one text call of an ideas run (02.8.1-T1, task Design): the <c>ideas.v1</c> batch of episode ideas.
/// </summary>
/// <remarks>
/// The provider outcome comes back as data (D7) and no exception escapes but the caller's own cancellation, so
/// the planner maps the case instead of catching it. Like <c>IBibleProposalGenerator</c>, the step settles the
/// call it made itself: one <c>usage_records</c> row, <c>operation = idea</c>, <c>episode_id</c> null
/// (<c>generation_runs.episode_id</c> is NULL for the series-level <c>ideas</c> kind, D2), and the planner only
/// decides the run's outcome.
/// </remarks>
public interface IIdeaGenerator
{
    /// <summary>Builds the data object of one idea batch and makes the call.</summary>
    /// <param name="request">The run, the series' context and the count.</param>
    /// <param name="ct">Cancels the call; the only exception a caller can see is its own cancellation.</param>
    /// <returns>The mapped outcome, carrying the usage whenever the provider reported it (D14).</returns>
    /// <exception cref="InvalidOperationException">
    /// The active price version carries no <c>ideas.max_output_tokens</c> limit, so the call has no ruled output
    /// ceiling (README T56: the ceiling is read from <c>price_versions.limits</c> only, never from configuration).
    /// </exception>
    ValueTask<TextCallResult<IdeasV1>> GenerateAsync(IdeaGenerationRequest request, CancellationToken ct);
}
