using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The status projection of 05.4.1-T2 §4: the store row, field by field, with nothing added. "Status: store
/// only" — the core never calls MPT, the relay or the disk to answer a poll (F19), and the blocks it serves
/// (<c>check</c>, <c>images</c>) are the verbatim ones the adapter stored.
/// </summary>
internal static class RenderStatusProjection
{
    internal static RenderStatusV1 From(RenderTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new RenderStatusV1(
            RenderNodeSchemas.RenderStatus,
            record.TaskId,
            record.RenderJobId,
            record.State,
            record.Stage,
            record.Percent,
            record.CancelRequested,
            record.SubmittedAt,
            record.BackendStartedAt,
            record.FinishedAt,
            record.Outcome,
            record.Output,
            record.Check,
            record.Images,
            record.MptLastSnapshot);
    }
}
