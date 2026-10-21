using NightTales.Application.Features.Stories.Approval;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Approval;

/// <summary>
/// The <c>Story:TrueCrime</c> rules (02.3.3-T1, 02 README T29): an attestation version a tick can be
/// matched against and a cited-source floor inside the F13 window, refused by name and never by echo.
/// </summary>
public sealed class StoryTrueCrimeOptionsValidatorTests
{
    private const string VersionKey = "'Story:TrueCrime:AttestationVersion'";
    private const string FloorKey = "'Story:TrueCrime:MinCitedSources'";

    [Fact]
    public void StoryTrueCrimeOptions_Defaults_AreValid()
    {
        var options = new StoryTrueCrimeOptions();

        options.AttestationVersion.ShouldBe("ta.v1");
        options.MinCitedSources.ShouldBe(2);
        StoryTrueCrimeOptionsValidator.Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ta.v2\u0007")]
    [InlineData("ta.v2\u0000")]
    [InlineData("ta.v1ta.v1ta.v1ta.v1ta.v1ta.v1ta.")]
    public void StoryTrueCrimeOptions_UnusableAttestationVersion_Fails(string version)
    {
        var problems = StoryTrueCrimeOptionsValidator.Validate(new StoryTrueCrimeOptions { AttestationVersion = version });

        problems.ShouldHaveSingleItem().ShouldContain(VersionKey);
    }

    /// <summary>A rejected value is named by its key, never published: the message must not carry it.</summary>
    [Fact]
    public void StoryTrueCrimeOptions_RejectedVersion_IsNeverEchoed()
    {
        var problems = StoryTrueCrimeOptionsValidator.Validate(
            new StoryTrueCrimeOptions { AttestationVersion = "  ta.v2  " });

        problems.ShouldHaveSingleItem().ShouldNotContain("ta.v2");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void StoryTrueCrimeOptions_MinCitedSourcesOutOfRange_Fails(int floor)
    {
        var problems = StoryTrueCrimeOptionsValidator.Validate(new StoryTrueCrimeOptions { MinCitedSources = floor });

        problems.ShouldHaveSingleItem().ShouldContain(FloorKey);
        problems[0].ShouldContain(
            $"{StoryTrueCrimeOptionsValidator.MinCitedSourcesFloor} and {StoryTrueCrimeOptionsValidator.MinCitedSourcesCeiling}");
    }

    [Fact]
    public void StoryTrueCrimeOptions_BothKeysInvalid_ListsBothProblems()
    {
        var problems = StoryTrueCrimeOptionsValidator.Validate(
            new StoryTrueCrimeOptions { AttestationVersion = " ", MinCitedSources = 0 });

        problems.Count.ShouldBe(2);
        problems.ShouldContain(problem => problem.Contains(VersionKey, StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains(FloorKey, StringComparison.Ordinal));
    }
}
