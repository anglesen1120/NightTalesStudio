using NightTales.Domain.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The true-crime framing signal of 02.8.1-T2 (task Design "Policy" 3, README F31/F33, Acceptance of
/// <c>ProposeAndPick_TrueCrime_SetsRequiresResearch</c>): a framing phrase, or a dated incident standing
/// near a crime noun, flags the candidate for research — and nothing else does.
/// </summary>
public sealed class IdeaTrueCrimeFramingTests
{
    private static readonly string[] FramingVi = ["câu chuyện có thật", "dựa trên sự thật"];
    private static readonly string[] FramingEn = ["based on a true story", "true crime"];
    private static readonly string[] CrimeNouns = ["murder", "án mạng", "giết người"];

    /// <summary>A framing phrase of either language claims a real basis, whatever its case or numbers.</summary>
    [Fact]
    public void IdeaTrueCrimeFraming_FramingPhrase_RequiresResearch()
    {
        IdeaTrueCrimeFraming.RequiresResearch("Một câu chuyện có thật về ngôi làng", FramingVi, CrimeNouns)
            .ShouldBeTrue();
        IdeaTrueCrimeFraming.RequiresResearch("Một góc nhìn DỰA TRÊN SỰ THẬT về ngôi làng", FramingVi, CrimeNouns)
            .ShouldBeTrue();
        IdeaTrueCrimeFraming.RequiresResearch("A case based on a true story", FramingEn, CrimeNouns)
            .ShouldBeTrue();
    }

    /// <summary>A plain angle is not a real incident, and a phrase's halves are not the phrase.</summary>
    [Fact]
    public void IdeaTrueCrimeFraming_PlainAngle_DoesNotRequireResearch()
    {
        IdeaTrueCrimeFraming.RequiresResearch("Một góc nhìn mới về nỗi sợ trong căn nhà cũ", FramingVi, CrimeNouns)
            .ShouldBeFalse();
        IdeaTrueCrimeFraming.RequiresResearch("A new angle on fear inside an old house", FramingEn, CrimeNouns)
            .ShouldBeFalse();
        IdeaTrueCrimeFraming.RequiresResearch("Not based on a true storybook", FramingEn, CrimeNouns)
            .ShouldBeFalse("the lexicon matches whole words");
    }

    /// <summary>A dated incident near a crime noun is the shape of a case, in either language.</summary>
    [Fact]
    public void IdeaTrueCrimeFraming_YearNearACrimeNoun_RequiresResearch()
    {
        IdeaTrueCrimeFraming.RequiresResearch("The murder of 1974 was never solved", [], ["murder"])
            .ShouldBeTrue();
        IdeaTrueCrimeFraming.RequiresResearch("Vụ án mạng năm 1974 ở làng", [], ["án mạng"])
            .ShouldBeTrue();
        IdeaTrueCrimeFraming.RequiresResearch("1974: hai kẻ giết người bị bắt", [], ["giết người"])
            .ShouldBeTrue();
    }

    /// <summary>A year is a setting detail until a crime noun is near it.</summary>
    [Fact]
    public void IdeaTrueCrimeFraming_YearWithoutANearbyCrimeNoun_DoesNotRequireResearch()
    {
        IdeaTrueCrimeFraming.RequiresResearch(
            "In 1974 the village celebrated its harvest with music and dancing in the square; the murder came much later",
            [],
            ["murder"]).ShouldBeFalse();
        IdeaTrueCrimeFraming.RequiresResearch("The murder of 1974", [], []).ShouldBeFalse();
        IdeaTrueCrimeFraming.RequiresResearch("The murder of 1974", [], ["   "]).ShouldBeFalse();
    }

    /// <summary>Only a four-digit <c>19xx</c>/<c>20xx</c> token is a dated incident.</summary>
    [Theory]
    [InlineData("The murder of 1874 was never solved")]
    [InlineData("The murder of 19745 was never solved")]
    [InlineData("The murder of the year 74 was never solved")]
    public void IdeaTrueCrimeFraming_NotADatedIncident_DoesNotRequireResearch(string text) =>
        IdeaTrueCrimeFraming.RequiresResearch(text, [], ["murder"]).ShouldBeFalse();

    /// <summary>A crime noun is a whole word: <c>murderer</c> is not <c>murder</c>.</summary>
    [Fact]
    public void IdeaTrueCrimeFraming_CrimeNoun_IsAWholeWord()
    {
        IdeaTrueCrimeFraming.RequiresResearch("In 1974 the murderer confessed", [], ["murder"])
            .ShouldBeFalse();
    }
}
