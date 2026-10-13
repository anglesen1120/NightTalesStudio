namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// The one question the validator asks the similarity scorer (02.5.3-T2 rule 4, F18): how much of the
/// statement is copied verbatim from the sources it cites. It is a request record, not an interface, so the
/// scorer stays owned by 02.5.6 and the validator pure — the job adapts
/// <c>SimilarityScorer.ScoreFact</c> to a <see cref="FactSheetValidator"/> <c>Func</c>.
/// </summary>
/// <param name="Statement">The normalised statement to score.</param>
/// <param name="CitedSources">The text of the sources the fact cites, in citation order.</param>
public sealed record ScoreFactRequest(string Statement, IReadOnlyList<SourceText> CitedSources);
