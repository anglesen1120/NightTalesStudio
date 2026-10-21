using NightTales.Application.Features.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Ideas;

/// <summary>
/// The batch dedupe key of an idea title (02.8.1-T1 plan §4 U1 step 4): two titles of one batch are the same
/// idea when they read the same, so their case, their Unicode composition and their whitespace may not differ
/// — but their letters must.
/// </summary>
public sealed class IdeaTitleKeyTests
{
    [Fact]
    public void Normalise_CaseOnlyDifference_IsTheSameKey() =>
        IdeaTitleKey.Normalise("The Last Train").ShouldBe(IdeaTitleKey.Normalise("the last train"));

    [Fact]
    public void Normalise_WhitespaceRuns_CollapseToOneSpace() =>
        IdeaTitleKey.Normalise("the\t last\n\ttrain").ShouldBe("the last train");

    [Fact]
    public void Normalise_LeadingAndTrailingWhitespace_IsTrimmed() =>
        IdeaTitleKey.Normalise("  the last train \n").ShouldBe("the last train");

    [Fact]
    public void Normalise_DecomposedAccents_ComposeToTheSameKey() =>
        // "cà phê" written with a combining grave (U+0300) is the same title to a reader as the composed one,
        // and Postgres compares the stored text byte-wise, so the key composes both to NFC first.
        IdeaTitleKey.Normalise("ca\u0300 phe\u0302").ShouldBe(IdeaTitleKey.Normalise("cà phê"));

    [Fact]
    public void Normalise_DifferentTitles_Differ() =>
        IdeaTitleKey.Normalise("The Last Train").ShouldNotBe(IdeaTitleKey.Normalise("The Last Tram"));

    [Fact]
    public void Normalise_Blank_IsEmpty() =>
        IdeaTitleKey.Normalise("   ").ShouldBeEmpty();

    [Fact]
    public void Normalise_Empty_IsEmpty() =>
        IdeaTitleKey.Normalise(string.Empty).ShouldBeEmpty();

    [Fact]
    public void Normalise_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => IdeaTitleKey.Normalise(null!));
}
