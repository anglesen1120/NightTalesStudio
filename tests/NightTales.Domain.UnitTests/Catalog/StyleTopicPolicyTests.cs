using NightTales.Domain.Catalog;
using NightTales.Domain.Series;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Catalog;

/// <summary>
/// <c>StyleTopicPolicy</c> (03.1.1-T2 Acceptance check, 03 README D2): a true-crime series needs a
/// true-crime template; a fiction series may use anything except one. A <c>null</c> hint is usable by
/// every fiction topic and by none of the true-crime ones.
/// </summary>
public sealed class StyleTopicPolicyTests
{
    public static TheoryData<Topic, Topic?, bool> Cells => new()
    {
        { Topic.TrueCrime, Topic.TrueCrime, true },
        { Topic.TrueCrime, Topic.Ghost, false },
        { Topic.TrueCrime, Topic.Spiritual, false },
        { Topic.TrueCrime, null, false },
        { Topic.Ghost, Topic.TrueCrime, false },
        { Topic.Ghost, Topic.Ghost, true },
        { Topic.Ghost, Topic.Spiritual, true },
        { Topic.Ghost, null, true },
        { Topic.Spiritual, Topic.TrueCrime, false },
        { Topic.Spiritual, Topic.Ghost, true },
        { Topic.Spiritual, Topic.Spiritual, true },
        { Topic.Spiritual, null, true },
    };

    [Theory]
    [MemberData(nameof(Cells))]
    public void StyleTopicPolicy_Table(Topic topic, Topic? topicHint, bool expected)
    {
        StyleTopicPolicy.IsAllowed(topic, topicHint).ShouldBe(expected);
    }
}
