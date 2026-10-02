using NightTales.MptAdapter.Tests.Fakes;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// SEC-11.3.5-T2-01: MPT controls its answer body, so the adapter must bound what it will read. The cap is
/// enforced from the declared <c>Content-Length</c> and, for a chunked answer (no declared length), by
/// counting the bytes read — and the body is never logged.
/// </summary>
public sealed class MptResponseLimitTests
{
    private const string MptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    [Fact]
    public async Task StatusAnswer_WithADeclaredLengthOverTheCap_FailsCleanly()
    {
        await using var harness = await AdapterHarness.StartAsync();
        harness.Mpt.Script(MptTaskId, 4, 40);
        harness.Mpt.OversizeDeclaredBytes = MptClient.MaxResponseBodyBytes + 1;

        var status = await harness.Client.GetStatusAsync(MptTaskId, Guid.CreateVersion7(), CancellationToken.None);

        status.IsError.ShouldBeTrue("an answer over the cap is a backend error, not a parse failure");
        status.FirstError.Code.ShouldBe(MptErrorCodes.ServerError);
        harness.Logs.Contains("over the").ShouldBeTrue("the cap breach is logged");
        harness.Logs.Contains("xxxx").ShouldBeFalse("not one byte of the body is logged (S7)");
    }

    [Fact]
    public async Task StatusAnswer_ChunkedOverTheCap_FailsCleanly()
    {
        await using var harness = await AdapterHarness.StartAsync();
        harness.Mpt.Script(MptTaskId, 4, 40);
        harness.Mpt.OversizeChunked = true;

        var status = await harness.Client.GetStatusAsync(MptTaskId, Guid.CreateVersion7(), CancellationToken.None);

        status.IsError.ShouldBeTrue("a chunked answer has no declared length and must still hit the cap");
        status.FirstError.Code.ShouldBe(MptErrorCodes.ServerError);
        harness.Logs.Contains("over the").ShouldBeTrue();
        harness.Logs.Contains("xxxx").ShouldBeFalse();
    }

    [Fact]
    public async Task PingAnswer_UnderTheCap_IsStillRead()
    {
        // The boundary control: the same fake with no oversize mode answers normally, so the cap does not
        // reject an ordinary answer.
        await using var harness = await AdapterHarness.StartAsync();

        (await harness.Client.PingAsync(CancellationToken.None)).ShouldBeTrue();
    }
}
