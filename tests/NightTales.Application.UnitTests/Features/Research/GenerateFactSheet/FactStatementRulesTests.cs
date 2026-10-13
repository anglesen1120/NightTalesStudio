using NightTales.Application.Features.Research.GenerateFactSheet;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Research.GenerateFactSheet;

/// <summary>
/// 02.5.4-T1: the shared statement rules (<c>FactStatementRules</c>) the edit path and
/// <see cref="FactSheetValidator"/> both call. The suite pins the same ordered rules the validator's own tests
/// cover — the raw cap before the control-character scan, NFC, whitespace collapse, the stored cap, C0/DEL/NUL,
/// the bidi controls and lone surrogates — because the edit body must reject exactly what a model statement is
/// rejected for (contract §12).
/// </summary>
public sealed class FactStatementRulesTests
{
    [Fact]
    public void TryNormalize_ComposesNfcAndCollapsesWhitespace()
    {
        // The input is the decomposed form, written with escapes so no editor or paste can compose it early:
        // e + combining circumflex (U+0302) + combining acute (U+0301), which NFC composes into U+1EBF.
        var reason = FactStatementRules.TryNormalize("  Tie\u0302\u0301ng   Viet\n\t hops  ", out var normalized);

        reason.ShouldBeNull();
        normalized.ShouldBe("Tiếng Viet hops", "NFC composes the marks, then every whitespace run collapses");
    }

    [Fact]
    public void TryNormalize_KeepsAStatementAtTheStoredCap()
    {
        var atCap = new string('a', FactSheetValidator.StatementMaxChars);

        FactStatementRules.TryNormalize(atCap, out var normalized).ShouldBeNull();
        normalized.ShouldBe(atCap);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void TryNormalize_EmptyOrWhitespaceOnly_ReturnsStatementInvalid(string? statement)
    {
        FactStatementRules.TryNormalize(statement, out _).ShouldBe(FactDropReasons.StatementInvalid);
    }

    [Fact]
    public void TryNormalize_OverTheStoredCap_ReturnsStatementInvalid()
    {
        var overCap = new string('a', FactSheetValidator.StatementMaxChars + 1);

        FactStatementRules.TryNormalize(overCap, out _).ShouldBe(FactDropReasons.StatementInvalid);
    }

    [Fact]
    public void TryNormalize_CollapsedLengthDecidesTheStoredCap()
    {
        // The cap applies to the stored (normalised) text, so a padded statement under it is kept.
        var padded = $"  {new string('a', FactSheetValidator.StatementMaxChars)}  ";

        FactStatementRules.TryNormalize(padded, out var normalized).ShouldBeNull();
        normalized.Length.ShouldBe(FactSheetValidator.StatementMaxChars);
    }

    [Fact]
    public void TryNormalize_OverTheRawCap_ReturnsStatementInvalid()
    {
        var overRawCap = new string('a', FactSheetValidator.RawStatementMaxChars + 1);

        FactStatementRules.TryNormalize(overRawCap, out _).ShouldBe(FactDropReasons.StatementInvalid);
    }

    [Fact]
    public void TryNormalize_RawCapIsScannedBeforeTheControlCharacters()
    {
        // The hostile input the raw cap exists for: a statement that could never normalise into a kept one is
        // rejected as statement_invalid, never scanned character by character (P6).
        var overRawCapWithNul = new string('a', FactSheetValidator.RawStatementMaxChars) + "\u0000";

        FactStatementRules.TryNormalize(overRawCapWithNul, out _).ShouldBe(FactDropReasons.StatementInvalid);
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\u0001b")]
    [InlineData("a\u001Fb")]
    [InlineData("a\u007Fb")]
    public void TryNormalize_NulDelOrC0Control_ReturnsControlCharactersInvalid(string statement)
    {
        FactStatementRules.TryNormalize(statement, out _).ShouldBe(FactDropReasons.ControlCharactersInvalid);
    }

    [Fact]
    public void TryNormalize_WhitespaceControls_AreCollapsedRatherThanRejected()
    {
        FactStatementRules.TryNormalize("first\tsecond\nthird", out var normalized).ShouldBeNull();
        normalized.ShouldBe("first second third");
    }

    [Theory]
    [InlineData("a\u061Cb")]
    [InlineData("a\u200Eb")]
    [InlineData("a\u200Fb")]
    [InlineData("a\u202Ab")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u2066b")]
    [InlineData("a\u2069b")]
    public void TryNormalize_BidiControl_ReturnsStatementInvalid(string statement)
    {
        FactStatementRules.TryNormalize(statement, out _).ShouldBe(FactDropReasons.StatementInvalid);
    }

    [Fact]
    public void TryNormalize_KeepsTheZeroWidthJoinerAndNonJoiner()
    {
        // Emoji sequences and Indic and Arabic scripts need them; only the reordering controls are rejected.
        FactStatementRules.TryNormalize("a\u200Db\u200Cc", out var normalized).ShouldBeNull();
        normalized.ShouldBe("a\u200Db\u200Cc");
    }

    /// <summary>
    /// The lone-surrogate shapes, built at run time. A lone surrogate written in an <c>[InlineData]</c> string never
    /// reaches the test body — the runner's theory data hands the test U+FFFD instead — so the shapes are data, the
    /// same workaround <c>LeaseFetchTasksValidatorTests.LoneSurrogateIds</c> uses.
    /// </summary>
    public static TheoryData<string> LoneSurrogates()
    {
        var high = new string('\uD800', 1);
        var low = new string('\uDC00', 1);

        return new TheoryData<string> { "a" + high + "b", "a" + low + "b", high };
    }

    [Theory]
    [MemberData(nameof(LoneSurrogates))]
    public void TryNormalize_LoneSurrogate_ReturnsStatementInvalid(string statement)
    {
        FactStatementRules.TryNormalize(statement, out _).ShouldBe(FactDropReasons.StatementInvalid);
    }

    [Fact]
    public void TryNormalize_KeepsAValidSurrogatePair()
    {
        FactStatementRules.TryNormalize("A 😀 fact.", out var normalized).ShouldBeNull();
        normalized.ShouldBe("A 😀 fact.");
    }

    [Fact]
    public void TryNormalize_ReportsNoReasonForAnOrdinaryStatement()
    {
        FactStatementRules.TryNormalize("The owner kept a note about the knock.", out var normalized).ShouldBeNull();
        normalized.ShouldBe("The owner kept a note about the knock.");
    }
}
