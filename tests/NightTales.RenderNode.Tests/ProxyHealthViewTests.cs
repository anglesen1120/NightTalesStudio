using System.Text.Json;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// <see cref="ProxyHealthView"/> reads the unchanged <c>proxy-health.v1</c> body (04 D8) embedded in health. It is
/// total by design: a body that is missing, of the wrong shape or self-contradicting must never make a node look
/// dispatchable.
/// </summary>
public sealed class ProxyHealthViewTests
{
    [Fact]
    public void OkStatus_WithNoReasons_IsOk()
    {
        var view = ProxyHealthView.From(Parse("""{"schema":"proxy-health.v1","status":"ok","reasons":[]}"""));

        view.Ok.ShouldBeTrue();
        view.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public void ErrorStatus_CarriesItsReasons()
    {
        var view = ProxyHealthView.From(
            Parse("""{"schema":"proxy-health.v1","status":"error","reasons":["vieneu_unreachable"]}"""));

        view.Ok.ShouldBeFalse();
        view.Reasons.ShouldBe(["vieneu_unreachable"]);
    }

    [Fact]
    public void OkStatus_WithAReason_IsNotOk()
    {
        // 04 D8 defines ok as status = "ok" AND reasons = []; a producer bug must not open the gate.
        var view = ProxyHealthView.From(
            Parse("""{"schema":"proxy-health.v1","status":"ok","reasons":["catalog_voice_missing"]}"""));

        view.Ok.ShouldBeFalse();
        view.Reasons.ShouldBe(["catalog_voice_missing"]);
    }

    [Theory]
    [InlineData("\"not an object\"")]
    [InlineData("""{"schema":"proxy-health.v1"}""")]
    [InlineData("""{"status":5,"reasons":"none"}""")]
    public void MalformedBody_IsNotOkAndNeverThrows(string json)
    {
        var view = ProxyHealthView.From(Parse(json));

        view.Ok.ShouldBeFalse();
        view.Reasons.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"status":"ok"}""")]
    [InlineData("""{"status":"ok","reasons":null}""")]
    [InlineData("""{"status":"ok","reasons":[42]}""")]
    [InlineData("""{"status":"ok","reasons":[]}""")]
    public void MissingOrMalformedReasons_FailsClosed(string json)
    {
        var view = ProxyHealthView.From(Parse(json));

        if (json.EndsWith("[]}", StringComparison.Ordinal))
        {
            // The one valid empty list is still ok (04 D8): only a missing/null/non-string shape fails closed.
            view.Ok.ShouldBeTrue();
            return;
        }

        view.Ok.ShouldBeFalse();
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
