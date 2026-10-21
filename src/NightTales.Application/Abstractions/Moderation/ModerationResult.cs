namespace NightTales.Application.Abstractions.Moderation;

/// <summary>The verdict of one moderation check (02 README D12).</summary>
/// <param name="Flagged"><see langword="true"/> when any checked text, or any chunk of one, was flagged.</param>
/// <param name="Categories">The union of the flagged categories over all texts and chunks (for example <c>sexual/minors</c>), ordinal-sorted.</param>
/// <param name="Scores">The highest score per category over all texts and chunks.</param>
public sealed record ModerationResult(
    bool Flagged,
    IReadOnlyList<string> Categories,
    IReadOnlyDictionary<string, double> Scores)
{
    /// <summary>The verdict of nothing to check, or of a clean check.</summary>
    public static ModerationResult Clean { get; } = new(false, [], new Dictionary<string, double>());
}
