using NightTales.Domain.Rendering;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Rendering;

/// <summary>
/// The state machine and the shape invariants of the <c>worker_nodes</c> aggregate (11.3.3-T1, 11 README
/// D13/F12/F26): only the health poller writes <c>down</c>/<c>active</c>, an operator may only drain or
/// activate, a heartbeat never moves backwards, and <c>Create</c>/<c>ApplyConfig</c> enforce the column
/// bounds the DB <c>CHECK</c>s mirror. The integration tests can only observe these rules through SQL, so
/// they are pinned here.
/// </summary>
public sealed class WorkerNodeStateTransitionTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private const string PinLower = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PinUpper = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private static readonly Uri HttpsBaseUrl = new("https://node.example:8443");

    [Fact]
    public void Create_StartsActive_AndNormalisesTheNameAndPin()
    {
        var node = WorkerNode.Create("  node-a  ", HttpsBaseUrl, 2, PinLower, "  RTX 4090  ").Value;

        node.State.ShouldBe(WorkerNodeState.Active);
        node.Name.ShouldBe("node-a");
        node.BaseUrl.ShouldBe(HttpsBaseUrl);
        node.Capacity.ShouldBe(2);
        node.TlsCertSha256.ShouldBe(PinUpper);
        node.GpuModel.ShouldBe("RTX 4090");
        node.LastHeartbeatAt.ShouldBeNull();
        node.ApiKeyCiphertext.ShouldBeNull();
        node.ApiKeyHint.ShouldBeNull();
    }

    [Fact]
    public void Create_AcceptsTheDevelopmentHttpNodeWithoutAPin()
    {
        var result = WorkerNode.Create("mock", new Uri("http://127.0.0.1:8090"), 2, null, null);

        result.IsError.ShouldBeFalse();
        result.Value.TlsCertSha256.ShouldBeNull();
        result.Value.BaseUrl.Scheme.ShouldBe(Uri.UriSchemeHttp);
    }

    [Fact]
    public void Create_RejectsAnHttpsNodeWithoutAPin()
    {
        var result = WorkerNode.Create("node-a", HttpsBaseUrl, 2, null, null);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(WorkerNode.TlsCertInvalidCode);
    }

    [Fact]
    public void Create_RejectsACredentialedBaseUrl()
    {
        // `nodes list` prints the URL verbatim, so a `user:pass@` authority would put a secret on stdout
        // (plan §5); the domain refuses it up front, like the other URL shape errors.
        var result = WorkerNode.Create(
            "node-a",
            new Uri("https://user:pass@node.example:8443"),
            2,
            PinUpper,
            null);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(WorkerNode.BaseUrlInvalidCode);

        var node = ActiveNode();
        node.ApplyConfig(new Uri("https://user:pass@other.example:9443"), 2, PinLower, null)
            .FirstError.Code.ShouldBe(WorkerNode.BaseUrlInvalidCode);
        node.BaseUrl.ShouldBe(HttpsBaseUrl, "a refused URL changes nothing");
    }

    [Fact]
    public void Create_RejectsANullRelativeOrNonHttpBaseUrl()
    {
        WorkerNode.Create("node-a", null, 2, PinUpper, null).FirstError.Code
            .ShouldBe(WorkerNode.BaseUrlInvalidCode);

        WorkerNode.Create("node-a", new Uri("/relative", UriKind.Relative), 2, PinUpper, null).FirstError.Code
            .ShouldBe(WorkerNode.BaseUrlInvalidCode);

        WorkerNode.Create("node-a", new Uri("ftp://node.example"), 2, PinUpper, null).FirstError.Code
            .ShouldBe(WorkerNode.BaseUrlInvalidCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]  // 63 characters
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")] // 65 characters
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]   // 64 non-hex
    public void Create_RejectsBadSha256(string pin)
    {
        var result = WorkerNode.Create("node-a", HttpsBaseUrl, 2, pin, null);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(WorkerNode.TlsCertInvalidCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void Create_RejectsCapacityOutOfRange(int capacity)
    {
        var result = WorkerNode.Create("node-a", HttpsBaseUrl, capacity, PinUpper, null);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(WorkerNode.CapacityInvalidCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")] // 65 characters
    public void Create_RejectsNameOutOfRange(string? name)
    {
        var result = WorkerNode.Create(name, HttpsBaseUrl, 2, PinUpper, null);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(WorkerNode.NameInvalidCode);
    }

    [Fact]
    public void Create_AcceptsTheNameAndCapacityBounds()
    {
        var name = new string('a', WorkerNode.NameMaxLength);

        var low = WorkerNode.Create(name, HttpsBaseUrl, WorkerNode.CapacityMin, PinUpper, null);
        var high = WorkerNode.Create(name, HttpsBaseUrl, WorkerNode.CapacityMax, PinUpper, null);

        low.IsError.ShouldBeFalse();
        low.Value.Name.ShouldBe(name);
        high.IsError.ShouldBeFalse();
        high.Value.Capacity.ShouldBe(WorkerNode.CapacityMax);
    }

    [Fact]
    public void Create_RejectsAnOverlongGpuModel_AndNullsABlankOne()
    {
        var tooLong = WorkerNode.Create("node-a", HttpsBaseUrl, 2, PinUpper, new string('g', 65));
        tooLong.IsError.ShouldBeTrue();
        tooLong.FirstError.Code.ShouldBe(WorkerNode.GpuModelInvalidCode);

        WorkerNode.Create("node-a", HttpsBaseUrl, 2, PinUpper, "   ").Value.GpuModel.ShouldBeNull();
    }

    [Fact]
    public void ApplyConfig_ReplacesTheValues_AndLeavesTheStateAndHeartbeatAlone()
    {
        var node = NodeIn(WorkerNodeState.Draining);
        node.RecordHeartbeat(FixedNow);

        var result = node.ApplyConfig(new Uri("https://other.example:9443"), 8, PinLower, "H100");

        result.IsError.ShouldBeFalse();
        node.BaseUrl.ShouldBe(new Uri("https://other.example:9443"));
        node.Capacity.ShouldBe(8);
        node.TlsCertSha256.ShouldBe(PinUpper);
        node.GpuModel.ShouldBe("H100");
        node.State.ShouldBe(WorkerNodeState.Draining);
        node.LastHeartbeatAt.ShouldBe(FixedNow);
    }

    [Fact]
    public void ApplyConfig_OnABadValue_ReportsTheError_AndChangesNothing()
    {
        var node = ActiveNode();

        var result = node.ApplyConfig(HttpsBaseUrl, WorkerNode.CapacityMax + 1, PinLower, null);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(WorkerNode.CapacityInvalidCode);
        node.Capacity.ShouldBe(2);
        node.TlsCertSha256.ShouldBe(PinUpper);
    }

    [Fact]
    public void SetOperatorState_ActiveToDraining_Succeeds()
    {
        var node = ActiveNode();

        node.SetOperatorState(WorkerNodeState.Draining).IsError.ShouldBeFalse();
        node.State.ShouldBe(WorkerNodeState.Draining);

        node.SetOperatorState(WorkerNodeState.Active).IsError.ShouldBeFalse();
        node.State.ShouldBe(WorkerNodeState.Active);
    }

    [Fact]
    public void SetOperatorState_ToTheSameState_IsAnIdempotentSuccess()
    {
        var node = ActiveNode();

        node.SetOperatorState(WorkerNodeState.Active).IsError.ShouldBeFalse();
        node.State.ShouldBe(WorkerNodeState.Active);
    }

    [Fact]
    public void SetOperatorState_RejectsDown_AndLeavesTheStateAlone()
    {
        var node = NodeIn(WorkerNodeState.Draining);

        var result = node.SetOperatorState(WorkerNodeState.Down);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(WorkerNode.OperatorStateInvalidCode);
        node.State.ShouldBe(WorkerNodeState.Draining);
    }

    [Fact]
    public void RecordHeartbeat_NeverMovesBackwards()
    {
        var node = ActiveNode();

        node.RecordHeartbeat(FixedNow).IsError.ShouldBeFalse();
        node.LastHeartbeatAt.ShouldBe(FixedNow);

        // A skewed or stale answer is ignored: the value is monotonic.
        node.RecordHeartbeat(FixedNow.AddSeconds(-30));
        node.LastHeartbeatAt.ShouldBe(FixedNow);

        // The same instant does not move it either.
        node.RecordHeartbeat(FixedNow);
        node.LastHeartbeatAt.ShouldBe(FixedNow);

        node.RecordHeartbeat(FixedNow.AddSeconds(30));
        node.LastHeartbeatAt.ShouldBe(FixedNow.AddSeconds(30));
    }

    [Theory]
    [InlineData(WorkerNodeState.Active)]
    [InlineData(WorkerNodeState.Draining)]
    [InlineData(WorkerNodeState.Down)]
    public void MarkDown_FromAnyState_GoesDown(WorkerNodeState from)
    {
        var node = NodeIn(from);

        node.MarkDown(FixedNow.AddMinutes(5)).IsError.ShouldBeFalse();

        node.State.ShouldBe(WorkerNodeState.Down);
        node.UpdatedAt.ShouldBe(FixedNow.AddMinutes(5));
    }

    [Fact]
    public void MarkActive_FromDraining_IsNoOp()
    {
        var node = NodeIn(WorkerNodeState.Draining);

        node.MarkActive(FixedNow.AddMinutes(5)).IsError.ShouldBeFalse();

        node.State.ShouldBe(WorkerNodeState.Draining, "health never ends an operator's drain (D12/F12)");
    }

    [Fact]
    public void MarkActive_FromDown_Activates()
    {
        var node = NodeIn(WorkerNodeState.Down);

        node.MarkActive(FixedNow.AddMinutes(5)).IsError.ShouldBeFalse();

        node.State.ShouldBe(WorkerNodeState.Active);
        node.UpdatedAt.ShouldBe(FixedNow.AddMinutes(5));
    }

    [Fact]
    public void MarkActive_FromActive_IsNoOp()
    {
        var node = ActiveNode();
        var updatedAt = node.UpdatedAt;

        node.MarkActive(FixedNow.AddMinutes(5)).IsError.ShouldBeFalse();

        node.State.ShouldBe(WorkerNodeState.Active);
        node.UpdatedAt.ShouldBe(updatedAt, "a steady node raises nothing");
    }

    private static WorkerNode ActiveNode() =>
        WorkerNode.Create("node-a", HttpsBaseUrl, 2, PinUpper, "RTX 4090").Value;

    private static WorkerNode NodeIn(WorkerNodeState state)
    {
        var node = ActiveNode();
        switch (state)
        {
            case WorkerNodeState.Draining:
                node.SetOperatorState(WorkerNodeState.Draining).IsError.ShouldBeFalse();
                break;
            case WorkerNodeState.Down:
                node.MarkDown(FixedNow).IsError.ShouldBeFalse();
                break;
            case WorkerNodeState.Active:
            default:
                break;
        }

        return node;
    }
}
