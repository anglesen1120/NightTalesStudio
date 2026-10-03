namespace NightTales.Application.Abstractions.Text;

/// <summary>The generation step a text call serves (02 README D21): it selects the model, the reasoning effort and the timeout.</summary>
public enum TextStep
{
    /// <summary>The story writer.</summary>
    Writer,

    /// <summary>The model review of a draft.</summary>
    Review,

    /// <summary>The revision of a reviewed draft.</summary>
    Revision,

    /// <summary>The fact-sheet extraction.</summary>
    Facts,

    /// <summary>The series-bible proposal.</summary>
    Bible,

    /// <summary>The idea generator.</summary>
    Ideas,

    /// <summary>The novelty check of an idea.</summary>
    Novelty,

    /// <summary>The source finder.</summary>
    Finder,
}
