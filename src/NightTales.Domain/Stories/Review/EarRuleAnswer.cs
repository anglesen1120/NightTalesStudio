namespace NightTales.Domain.Stories.Review;

/// <summary>The model's answer to one naturalness question (02.1.5-T1 Q1–Q6) of a <c>review.v1</c>.</summary>
/// <param name="QuestionId">The question's stable id, <c>Q1</c> to <c>Q6</c>.</param>
/// <param name="Passed"><see langword="true"/> when no sentence of the story fails the question.</param>
/// <param name="Segment">The worst sentence's D22 address; <c>meta</c> for a passed answer.</param>
/// <param name="Evidence">A quote of the draft, at most 200 characters; empty for a passed answer.</param>
public sealed record EarRuleAnswer(string QuestionId, bool Passed, string Segment, string Evidence);
