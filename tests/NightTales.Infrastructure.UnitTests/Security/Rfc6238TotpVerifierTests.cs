using System.Text;
using NightTales.Infrastructure.Security;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// The RFC 6238 algorithm of 09.10.4-T1 (Tests table): the published vectors, the ± one-step window and
/// the refusal of a step two away.
/// </summary>
/// <remarks>
/// No database and no host: <see cref="Rfc6238"/> is a pure function of the key, the step and the digits.
/// The published vectors of RFC 6238 Appendix B use the ASCII key <c>12345678901234567890</c> with SHA-1;
/// their six-digit column is what BD4 fixes.
/// </remarks>
public sealed class Rfc6238TotpVerifierTests
{
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void MatchesRfc6238Vectors(long unixSeconds, string expected)
    {
        Rfc6238.Compute(RfcKey, unixSeconds / Rfc6238.DefaultStepSeconds).ShouldBe(expected);
    }

    [Fact]
    public void RejectsWrongCode()
    {
        Rfc6238.TryMatch(RfcKey, 1, Rfc6238.DefaultStepSeconds, "287083", out _).ShouldBeFalse();
    }

    [Fact]
    public void AcceptsWindowPlusMinusOne()
    {
        var step = 2L;

        Rfc6238.TryMatch(
                RfcKey,
                step,
                Rfc6238.DefaultStepSeconds,
                Rfc6238.Compute(RfcKey, step - 1),
                out var previous)
            .ShouldBeTrue();
        previous.ShouldBe(step - 1, "the accepted step is the one the code belongs to");

        Rfc6238.TryMatch(
                RfcKey,
                step,
                Rfc6238.DefaultStepSeconds,
                Rfc6238.Compute(RfcKey, step),
                out var current)
            .ShouldBeTrue();
        current.ShouldBe(step);

        Rfc6238.TryMatch(
                RfcKey,
                step,
                Rfc6238.DefaultStepSeconds,
                Rfc6238.Compute(RfcKey, step + 1),
                out var next)
            .ShouldBeTrue();
        next.ShouldBe(step + 1);
    }

    [Fact]
    public void RejectsStepMinusTwo()
    {
        Rfc6238.TryMatch(
                RfcKey,
                2,
                Rfc6238.DefaultStepSeconds,
                Rfc6238.Compute(RfcKey, 0),
                out _)
            .ShouldBeFalse("the window is ±1, so a code from step −2 is outside it");
    }

    [Fact]
    public void StepAt_UsesTheConfiguredStep()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(90);

        Rfc6238.StepAt(now, Rfc6238.DefaultStepSeconds).ShouldBe(3);
        Rfc6238.StepAt(now, 60).ShouldBe(1);
    }
}
