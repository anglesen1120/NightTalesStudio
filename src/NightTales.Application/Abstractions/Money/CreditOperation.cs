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

    /// <summary>
    /// <c>render</c>: one render job (09.6.1-T1, W2 CR-2). The whole job is reserved once, at the
    /// <c>POST /episodes/{id}/render</c> command (BD5 step 10), against the worst-case image cost of
    /// <c>IImageCostEstimator</c> (03 D8), and settled by the 09.6.3-T2 ingest against the usage rows of the
    /// same reservation.
    /// </summary>
    Render,
}
