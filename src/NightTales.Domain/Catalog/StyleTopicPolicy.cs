using NightTales.Domain.Series;

namespace NightTales.Domain.Catalog;

/// <summary>
/// Which style templates a series topic may use (03 README D2): a true-crime series needs a
/// true-crime template, and a fiction series may not use one.
/// </summary>
public static class StyleTopicPolicy
{
    /// <summary>True when a series of <paramref name="topic"/> may use a template with <paramref name="topicHint"/>.</summary>
    public static bool IsAllowed(Topic topic, Topic? topicHint) => topic == Topic.TrueCrime
        ? topicHint == Topic.TrueCrime
        : topicHint != Topic.TrueCrime;
}
