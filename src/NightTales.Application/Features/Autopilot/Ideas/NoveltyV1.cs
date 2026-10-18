namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The <c>novelty.v1</c> model answer (02.8.1-T2, task Design "Novelty"): one score row per candidate of the
/// batch. The strict schema is <c>prompts/schemas/novelty.v1.json</c> and the deserialiser is the adapter's
/// snake_case options, the same convention as <see cref="IdeasV1"/>: the member names are the wire names.
/// </summary>
/// <remarks>
/// The schema requires every property of every row and forbids extras, so nothing here is nullable except the
/// overlap reference: a JSON <c>null</c>, a missing member or an unknown one is a deserialisation failure the
/// client maps to <c>InvalidOutput</c>, which is the degrade-after-one-retry path of the checker.
/// </remarks>
/// <param name="Scores">The rows, in the model's order; a row's <see cref="NoveltyScoreV1.Index"/> is the candidate's 0-based position in the batch.</param>
public sealed record NoveltyV1(IReadOnlyList<NoveltyScoreV1> Scores);

/// <summary>
/// One candidate's novelty verdict (02.8.1-T2, task Design "Novelty"): how close the judge found it to the
/// series' past, and the two craft components the scorer reuses.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MaxOverlapEpisodeId"/> is the model's answer to <c>max_overlap_episode_id</c>, which the prompt
/// requires to be the <b>1-based</b> index of the closest past episode as a decimal string (models garble
/// UUIDs, D6/F12), or <c>null</c> when nothing overlaps. It is a string here for exactly that reason: the
/// server resolves the index to an id, and a garbled or out-of-range value degrades to <c>null</c> rather than
/// failing the answer.
/// </para>
/// <para>
/// <see cref="Reason"/> is the model's one-line justification: server-side data of the stored
/// <c>novelty</c> document, capped and sanitised before it is written, and never logged (D19).
/// </para>
/// </remarks>
/// <param name="Index">The candidate's 0-based position in the batch.</param>
/// <param name="MaxOverlapEpisodeId">The 1-based index of the closest past episode as text, or <see langword="null"/>.</param>
/// <param name="Score">How similar the judge found the idea to the past, 0…1 (1 = the same story already told).</param>
/// <param name="Fit">How well the idea fits the series' topic, 0…1.</param>
/// <param name="HookStrength">How strong the judge found the opening hook, 0…1.</param>
/// <param name="Reason">The judge's justification; stored, capped and never logged.</param>
public sealed record NoveltyScoreV1(
    int Index,
    string? MaxOverlapEpisodeId,
    decimal Score,
    decimal Fit,
    decimal HookStrength,
    string Reason);
