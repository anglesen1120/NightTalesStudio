using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.SeriesBible.Abstractions;

/// <summary>
/// The D5 manifest keys and the wire name of the series-bible proposal step (02.6.3-T2, plan §7.1 step 3).
/// </summary>
/// <remarks>
/// One place, because three callers must agree on them: the Infrastructure generator that reads the prompt and
/// the schema out of the prompt catalog, the outbox handler that composes the run's <c>prompt_version</c>, and
/// the tests. The keys are relative to <c>prompts/</c>, as <c>IPromptCatalog</c> requires; <see cref="SchemaFile"/>
/// is the file name the catalog's <c>GetSchema</c> takes, <see cref="SchemaKey"/> the manifest key the composite
/// version names and <see cref="SchemaName"/> the logical name the provider sees.
/// </remarks>
public static class BibleProposalPrompts
{
    /// <summary>The system prompt of the proposal step, <c>bible/propose.v1.md</c>.</summary>
    public const string SystemKey = "bible/propose.v1.md";

    /// <summary>The manifest key of the strict output schema, <c>schemas/bible-proposal.v1.json</c>.</summary>
    public const string SchemaKey = "schemas/bible-proposal.v1.json";

    /// <summary>The schema's file name under <c>prompts/schemas/</c>, as <c>IPromptCatalog.GetSchema</c> takes it.</summary>
    public const string SchemaFile = "bible-proposal.v1.json";

    /// <summary>The schema's logical name; the adapter maps its dot to the underscore OpenAI allows.</summary>
    public const string SchemaName = "bible-proposal.v1";

    /// <summary>
    /// The provider prompt-cache key (02 README D7/O9): preset-scoped and constant, never per series or per
    /// episode, so every proposal of every series shares the static system-prompt prefix.
    /// </summary>
    public const string CacheKey = "bible:propose";
}

/// <summary>
/// The D14 operation every paid call of a bible-proposal run is metered under (02 README D14, 02.6.3-T2).
/// </summary>
/// <remarks>
/// <c>usage_records.operation</c> and the two OpenAI counters of plan §11 (<c>openai_tokens_total</c>,
/// <c>openai_cost_usd_total</c>) all read it, so it is declared once, in Application, where the generator and the
/// job can both name it.
/// </remarks>
public static class BibleProposalOperations
{
    /// <summary>The <c>operation</c> of the one paid proposal call.</summary>
    public const string BibleProposal = "bible_proposal";
}

/// <summary>
/// The inputs of one bible-proposal call (02.6.3-T2 plan §7.1 step 3, task Design): the run it is billed to, the
/// series' language and topic, the bible as it stands, and the approved episode — its title, its D3 structure
/// block and its story body.
/// </summary>
/// <remarks>
/// <para>
/// The caller loads all of it, so the generator reads no database: it serialises the one D6 data object, takes
/// the step prompt and the schema from the prompt catalog and makes the call. Nothing here carries an id of a
/// character, a place or a rule — the model never sees a UUID (D6's alias principle) and the episode id travels
/// on the run, not in the prompt.
/// </para>
/// <para>
/// <see cref="Language"/> and <see cref="Topic"/> are the snake_case spellings the data object's control keys use
/// (<c>vi</c>, <c>true_crime</c>), exactly as the fact-extraction step spells its language.
/// </para>
/// </remarks>
/// <param name="Run">The generation run the call belongs to: attempt, reservation and the episode for D14 attribution.</param>
/// <param name="Language">The series' content language, snake_case.</param>
/// <param name="Topic">The series' narrative topic, snake_case.</param>
/// <param name="Bible">The bible as stored; its newest summaries travel, without ids.</param>
/// <param name="Structure">The D3 structure tags of the approved story version, plus its body beats.</param>
/// <param name="StoryTitle">The approved story's title.</param>
/// <param name="StoryBody">The approved story's body; the generator truncates it to the configured ceiling.</param>
public sealed record BibleProposalRequest(
    RunContext Run,
    string Language,
    string Topic,
    SeriesBibleContent Bible,
    StoryStructureTags Structure,
    string StoryTitle,
    string StoryBody);

/// <summary>
/// The one text call of a bible-proposal run (02.6.3-T2, task Design): the <c>bible-proposal.v1</c> draft of one
/// approved episode.
/// </summary>
/// <remarks>
/// The provider outcome comes back as data (D7) and no exception escapes but the caller's own cancellation, so
/// the job maps the case instead of catching it. The call already carries its D14 usage row: like
/// <c>IStoryWriter</c>, the step settles the call it made itself, and the job only settles the run.
/// </remarks>
public interface IBibleProposalGenerator
{
    /// <summary>Builds the data object of one proposal and makes the call.</summary>
    /// <param name="request">The run, the bible and the approved episode.</param>
    /// <param name="ct">Cancels the call; the only exception a caller can see is its own cancellation.</param>
    /// <returns>The mapped outcome, carrying the usage whenever the provider reported it (D14).</returns>
    /// <exception cref="InvalidOperationException">
    /// The active price version is missing, so the call has neither rates nor its ruled output ceiling (README
    /// T56: the ceiling is read from <c>price_versions.limits</c> only, never from configuration).
    /// </exception>
    ValueTask<TextCallResult<BibleProposalV1>> GenerateAsync(BibleProposalRequest request, CancellationToken ct);
}
