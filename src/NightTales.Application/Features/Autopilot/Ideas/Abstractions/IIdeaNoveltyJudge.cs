using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Application.Features.Autopilot.Ideas.Abstractions;

/// <summary>
/// The D5 manifest keys and the wire name of the novelty-judge step (02.8.1-T2, task Design "Novelty").
/// </summary>
/// <remarks>
/// One place, because three callers must agree on them: the Infrastructure judge that reads the prompt and the
/// schema out of the prompt catalog, the caller that composes the run's <c>prompt_version</c>, and the tests.
/// The keys are relative to <c>prompts/</c>, <see cref="SchemaFile"/> is the name
/// <c>IPromptCatalog.GetSchema</c> takes, <see cref="SchemaKey"/> is the manifest key and
/// <see cref="SchemaName"/> the logical name the provider sees (the <see cref="IdeaPrompts"/> shape).
/// </remarks>
public static class NoveltyPrompts
{
    /// <summary>The system prompt of the judge step, <c>ideas/novelty.v1.md</c>.</summary>
    public const string SystemKey = "ideas/novelty.v1.md";

    /// <summary>The manifest key of the strict output schema, <c>schemas/novelty.v1.json</c>.</summary>
    public const string SchemaKey = "schemas/novelty.v1.json";

    /// <summary>The schema's file name under <c>prompts/schemas/</c>, as <c>IPromptCatalog.GetSchema</c> takes it.</summary>
    public const string SchemaFile = "novelty.v1.json";

    /// <summary>The schema's logical name; the adapter maps its dot to the underscore OpenAI allows.</summary>
    public const string SchemaName = "novelty.v1";

    /// <summary>
    /// The provider prompt-cache key (02 README D7/O9): constant, never per series or per slot, so every judge
    /// call of every series shares the static system-prompt prefix.
    /// </summary>
    public const string CacheKey = "ideas:novelty";
}

/// <summary>
/// The inputs of the one novelty-judge call of a batch (02.8.1-T2, task Design "Novelty"): the run it is billed
/// to, the series' language and topic, the candidates to score and the corpus episodes the judge may name as
/// the closest past episode.
/// </summary>
/// <remarks>
/// <para>
/// The judge reads no database: the checker loads the corpus through <see cref="IIdeaCorpusProvider"/> and the
/// Infrastructure judge serialises the one D6 data object. <see cref="PastEpisodes"/> travels by <b>1-based
/// index</b> and never by id — a model garbles UUIDs (D6/F12) — and no id, URL or owner text beyond these
/// fields may reach the provider (D19).
/// </para>
/// <para>
/// Everything here is untrusted material: the system prompt states that the data object is data and never
/// instructions, and the candidates are the model's own earlier output.
/// </para>
/// </remarks>
/// <param name="Run">The run the call belongs to: its id is the correlation prefix (<c>{generationRunId}:novelty:{attempt}</c>) and its price version meters the call.</param>
/// <param name="Language">The series' content language, snake_case.</param>
/// <param name="Topic">The series' narrative topic, snake_case.</param>
/// <param name="Ideas">The candidates to score, in the batch's order: the 0-based <c>index</c> of the answer is the position here.</param>
/// <param name="PastEpisodes">The corpus episodes the judge may name, in the corpus' order: the 1-based <c>index</c> of an answer is the position here.</param>
public sealed record NoveltyJudgeRequest(
    RunContext Run,
    string Language,
    string Topic,
    IReadOnlyList<IdeaCandidate> Ideas,
    IReadOnlyList<CorpusEpisode> PastEpisodes);

/// <summary>
/// The one paid novelty-judge call of a batch (02.8.1-T2, task Design "Novelty").
/// </summary>
/// <remarks>
/// <para>
/// One call for the whole batch, never one per candidate: the model sees the candidates together, so it cannot
/// call two similar candidates both fresh, and the batch pays once. <c>fit</c> and <c>hook_strength</c> travel
/// in the same answer, which is why the scorer needs no second call (plan §3.4).
/// </para>
/// <para>
/// The implementation makes <b>one</b> attempt and meters that attempt itself (D14, <c>operation = idea</c>),
/// like <see cref="IIdeaGenerator"/>: the D7 retry for a refused output lives in the caller
/// (<see cref="IIdeaNoveltyChecker"/>), which is what the run's attempt counter and the correlation id read.
/// The provider outcome comes back as data and no exception escapes but the caller's own cancellation.
/// </para>
/// </remarks>
public interface IIdeaNoveltyJudge
{
    /// <summary>Makes the one paid call of the batch.</summary>
    /// <param name="request">The run, the series' language and topic, the candidates and the corpus episodes.</param>
    /// <param name="ct">Cancels the call; the only exception a caller can see is its own cancellation.</param>
    /// <returns>The mapped outcome, carrying the usage whenever the provider reported it (D14).</returns>
    /// <exception cref="InvalidOperationException">
    /// The active price version carries no <c>novelty.max_output_tokens</c> limit, so the call has no ruled
    /// output ceiling (README T56: the ceiling is read from <c>price_versions.limits</c> only).
    /// </exception>
    ValueTask<TextCallResult<NoveltyV1>> JudgeAsync(NoveltyJudgeRequest request, CancellationToken ct);
}
