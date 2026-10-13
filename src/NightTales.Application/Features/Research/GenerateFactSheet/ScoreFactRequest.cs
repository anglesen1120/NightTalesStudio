namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// The one question the validator asks the similarity scorer (02.5.3-T2 rule 4, F18): how much of the
/// statement is copied verbatim from the run's url-source corpus. It is a request record, not an interface, so
/// the scorer stays owned by 02.5.6 and the validator pure — the job adapts <c>SimilarityScorer.ScoreFact</c>
/// to a <see cref="FactSheetValidator"/> <c>Func</c>.
/// </summary>
/// <param name="Statement">The normalised statement to score.</param>
/// <param name="Corpus">
/// The text of every url source of the run's plan, in id order — the whole corpus, not the sources the fact
/// cites (SEC-5): a statement copied from source A while citing a note alias or another source must still be
/// caught. The user note is never part of it (F18).
/// </param>
public sealed record ScoreFactRequest(string Statement, IReadOnlyList<SourceText> Corpus);
