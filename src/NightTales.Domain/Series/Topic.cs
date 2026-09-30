namespace NightTales.Domain.Series;

/// <summary>
/// Narrative topic of a series (09 README D16). Stored as snake_case text with a <c>CHECK</c>
/// constraint, so <see cref="TrueCrime"/> is the value <c>true_crime</c>.
/// </summary>
public enum Topic
{
    Ghost,
    Spiritual,
    TrueCrime,
}

/// <summary>Topic predicates shared by the series aggregate and the autopilot gates (09 README D16).</summary>
public static class TopicExtensions
{
    /// <summary>True for the fictional topics; true crime is the only non-fiction one.</summary>
    public static bool IsFiction(this Topic topic) => topic != Topic.TrueCrime;
}
