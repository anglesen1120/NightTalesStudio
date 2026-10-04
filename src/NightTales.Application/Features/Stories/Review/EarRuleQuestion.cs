using NightTales.Domain.Common;

namespace NightTales.Application.Features.Stories.Review;

/// <summary>
/// One functional naturalness question the review answers (02.1.5-T1 §Design 4, 02 T88): a stable id and the
/// model-facing text. The text is untrusted-neutral system data, sent under <c>ear_rules.questions</c> of the review
/// data object; the id is a control key.
/// </summary>
/// <param name="QuestionId">The stable id, <c>Q1</c> to <c>Q6</c>.</param>
/// <param name="Text">The question as the model reads it.</param>
public sealed record EarRuleQuestion(string QuestionId, string Text);

/// <summary>The six questions of 02.1.5-T1 §4 (source of truth: that task file); none of them is objective.</summary>
public static class EarRuleQuestions
{
    private static readonly EarRuleQuestion[] Questions =
    [
        new("Q1", "Function: does this sentence move one of — fear, clue, choice, consequence? If none, say which one it should move and how."),
        new("Q2", "Redundancy: does the scene already show this (an emotion named that the action already carries, a reaction the image already gives)? If yes, the sentence is the problem, not the word."),
        new("Q3", "Explanation: is the sentence explaining what the reader/listener can already infer? (tension, motive, atmosphere) — prefer the shorter version that trusts the listener."),
        new("Q4", "Overused phrase: if a phrase from the overused-phrases list appears, does it do work here (once, deliberately) or is it filler? One deliberate use is allowed."),
        new("Q5", "Read-aloud: read the paragraph at narration pace. Where do you stumble or want to go back? Quote the exact clause."),
        new("Q6", "Register (vi): is this written Vietnamese rather than spoken? Name the written construction and give the spoken alternative."),
    ];

    /// <summary>The questions that apply to a content language: all six for Vietnamese, Q1–Q5 for English (Q6 is the Vietnamese register check).</summary>
    /// <param name="language">The content language.</param>
    /// <returns>The questions in id order.</returns>
    public static IReadOnlyList<EarRuleQuestion> For(Language language) =>
        language == Language.Vi ? Questions : [.. Questions.Where(question => question.QuestionId != "Q6")];
}
