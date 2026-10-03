namespace NightTales.Application.Abstractions.Money;

/// <summary>What a credit reservation pays for (02 README D1); the wire values are the snake_case names.</summary>
public enum CreditOperation
{
    /// <summary><c>story_generate</c>: one story generation run.</summary>
    StoryGenerate,

    /// <summary><c>fact_sheet</c>: one fact-sheet extraction run.</summary>
    FactSheet,

    /// <summary><c>bible_proposal</c>: one series-bible proposal run.</summary>
    BibleProposal,

    /// <summary><c>idea</c>: one idea-generation run.</summary>
    Idea,

    /// <summary><c>source_find</c>: one source-finder run.</summary>
    SourceFind,
}
