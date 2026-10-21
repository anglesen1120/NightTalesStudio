using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The fixed D15 S4 sentences (05.4.1-T2 §5): one per 05 D5 outcome code, ≤ 200 characters, never content — the
/// adapter puts the sentence of the mapped code into <c>outcome.message</c>, so this is the consumer-visible text.
/// </summary>
public sealed class OutcomeMessagesTests
{
    [Fact]
    public void AllCodes_HaveFixedSentence()
    {
        RenderOutcomeCodes.All.Count.ShouldBe(10);

        foreach (var code in RenderOutcomeCodes.All)
        {
            var message = OutcomeMessages.For(code);

            message.ShouldNotBeNullOrWhiteSpace(code);
            message.Length.ShouldBeLessThanOrEqualTo(200, code);
            // The message never carries a path, a URI or node internals (§1, F11).
            message.ShouldNotContain("/", Case.Sensitive, code);
        }

        RenderOutcomeCodes.All
            .Select(OutcomeMessages.For)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(RenderOutcomeCodes.All.Count, "every outcome code has its own sentence");

        OutcomeMessages.For("a_code_that_does_not_exist").ShouldNotBeNullOrWhiteSpace();
    }
}
