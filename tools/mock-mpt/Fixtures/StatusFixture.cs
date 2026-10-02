using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt.Fixtures;

/// <summary>
/// One D14 <c>render-status.v1</c> fixture the mock replays (11.1.2-T1 Design: <c>__fixture_&lt;name&gt;__</c>
/// or <c>MOCK_FIXTURE</c>). The body is served verbatim except the fields the contract generates for the
/// replaying task: <c>task_id</c>, <c>render_job_id</c>, <c>submitted_at</c> and <c>started_at</c>.
/// </summary>
public sealed class StatusFixture
{
    private readonly RenderStatusV1 _status;

    internal StatusFixture(string id, RenderStatusV1 status)
    {
        Id = id;
        _status = status;
    }

    /// <summary>The fixture id (file name without <c>.json</c>), e.g. <c>render-status.v1.running-voice</c>.</summary>
    public string Id { get; }

    /// <summary>Applies the fixture's state to the replaying task's stored row.</summary>
    /// <remarks>
    /// The caller passes the live stored row, so the record's idempotency fields
    /// (<c>BodySha256</c>, <c>RenderJobId</c>, <c>SubmittedAt</c>) and its backend acknowledgement are never
    /// clobbered — this only replaces the observed state, stage, progress and blocks.
    /// </remarks>
    public RenderTaskRecord Apply(RenderTaskRecord stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return stored with
        {
            State = _status.State,
            Stage = _status.Stage,
            Percent = _status.Percent,
            CancelRequested = _status.CancelRequested,
            Outcome = _status.Outcome,
            Output = _status.Output,
            Check = _status.Check,
            Images = _status.Images,
            FinishedAt = _status.FinishedAt,
            MptLastSnapshot = _status.Mpt,
        };
    }
}
