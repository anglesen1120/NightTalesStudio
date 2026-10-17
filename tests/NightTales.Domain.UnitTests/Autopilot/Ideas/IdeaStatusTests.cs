using System.Text.Json;
using NightTales.Domain.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The stored form of <see cref="IdeaStatus"/> (02.8.1 plan §3.1): the four lifecycle states, written as the
/// snake_case text <c>episode_ideas.status</c> holds.
/// </summary>
public sealed class IdeaStatusTests
{
    [Fact]
    public void IdeaStatus_StoredNames_AreTheSnakeCaseValues()
    {
        // The EF EnumToSnakeCaseStringConverter and IdeaJson both use this policy, so the member names are the
        // wire values: Proposed -> proposed, and so on.
        Stored(IdeaStatus.Proposed).ShouldBe("proposed");
        Stored(IdeaStatus.Picked).ShouldBe("picked");
        Stored(IdeaStatus.Rejected).ShouldBe("rejected");
        Stored(IdeaStatus.Used).ShouldBe("used");
    }

    [Fact]
    public void IdeaStatus_HasExactlyTheFourLifecycleStates() =>
        Enum.GetValues<IdeaStatus>().Length.ShouldBe(4);

    [Fact]
    public void IdeaStatus_EveryStoredName_FitsTheColumn()
    {
        // status is varchar(16) and the table's check constraint is written from these four names.
        Enum.GetValues<IdeaStatus>()
            .ShouldAllBe(status => Stored(status).Length <= 16);
    }

    private static string Stored(IdeaStatus status) => JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
}
