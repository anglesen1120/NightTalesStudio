using System.Text.Json;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.RenderNode.Tests.Fixtures;

/// <summary>
/// Builds the store rows the task-scoped routes read. Values are deliberately plain: a test asserts one stored
/// fact at a time, and the blocks are built from JSON so they keep the shape a real adapter would store.
/// </summary>
internal static class Records
{
    private static readonly DateTimeOffset SubmittedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    internal static Guid NewTaskId() => Guid.CreateVersion7();

    internal static RenderTaskRecord Queued(Guid taskId, Guid renderJobId) => new(
        TaskId: taskId,
        RenderJobId: renderJobId,
        BodySha256: new string('c', 64),
        BackendTaskId: null,
        SubmittedAt: SubmittedAt,
        BackendStartedAt: null,
        State: RenderStates.Queued,
        Stage: RenderStages.Queued,
        Percent: 0,
        CancelRequested: false,
        Outcome: null,
        Output: null,
        Check: null,
        Images: null,
        FinishedAt: null,
        CleanedAt: null,
        MptLastSnapshot: null);

    internal static RenderTaskRecord Running(Guid taskId, Guid renderJobId, string stage = RenderStages.Voice, int percent = 20) =>
        Queued(taskId, renderJobId) with
        {
            State = RenderStates.Running,
            Stage = stage,
            Percent = percent,
            BackendTaskId = "mpt-task-1",
            BackendStartedAt = SubmittedAt.AddSeconds(1),
            MptLastSnapshot = new MptStateV1(4, percent, null, []),
        };

    internal static RenderTaskRecord Succeeded(
        Guid taskId,
        Guid renderJobId,
        RenderOutputV1? output = null,
        JsonElement? check = null,
        JsonElement? images = null,
        DateTimeOffset? cleanedAt = null) =>
        Queued(taskId, renderJobId) with
        {
            State = RenderStates.Succeeded,
            Stage = RenderStages.Done,
            Percent = 100,
            BackendTaskId = "mpt-task-1",
            BackendStartedAt = SubmittedAt.AddSeconds(1),
            FinishedAt = SubmittedAt.AddMinutes(3),
            CleanedAt = cleanedAt,
            Output = output ?? Output(),
            Check = check,
            Images = images,
            MptLastSnapshot = new MptStateV1(1, 100, null, []),
        };

    internal static RenderTaskRecord Failed(Guid taskId, Guid renderJobId, string outcomeCode) =>
        Queued(taskId, renderJobId) with
        {
            State = RenderStates.Failed,
            Stage = RenderStages.Voice,
            Percent = 20,
            FinishedAt = SubmittedAt.AddMinutes(3),
            Outcome = new RenderOutcomeV1(outcomeCode, "audio", OutcomeMessages.For(outcomeCode)),
            MptLastSnapshot = new MptStateV1(-1, 20, "audio", []),
        };

    /// <summary>
    /// The <c>output</c> block of a succeeded task. The hashes match <see cref="FakeRenderBackend"/>'s defaults on
    /// purpose: the fake stands in for the seam that reads <c>check.output.sha256</c>, so a test can assert that the
    /// served header is the seam's value and not a re-hash.
    /// </summary>
    internal static RenderOutputV1 Output(long videoBytes = 4, string? videoSha256 = null, bool withSubtitles = true) => new(
        Video: new RenderFileV1(videoBytes, videoSha256 ?? new string('a', 64), Cues: null),
        Subtitles: withSubtitles ? new RenderFileV1(3, new string('b', 64), Cues: 12) : null,
        NarrationSeconds: 301.37);

    /// <summary>The <c>render-check.v1</c> block a real adapter stores, reduced to the fields a status test needs.</summary>
    internal static JsonElement CheckBlock() => JsonDocument.Parse(
        $$"""
        {"schema":"render-check.v1","tool_version":"1.0.0","ffmpeg_version":"9.0.2",
         "input":{"file":"final-1.mp4","bytes":98123456,"sha256":"{{new string('d', 64)}}"},
         "output":{"file":"final-1.norm.mp4","bytes":98100000,"sha256":"027b8e0f","normalized":true},
         "warnings":[],"errors":[]}
        """).RootElement.Clone();

    /// <summary>An <c>images.v1</c> block as 03 D6 defines it (the relay report of a finished task).</summary>
    internal static JsonElement ImagesBlock() => JsonDocument.Parse(
        """
        {"schema":"images.v1","planned":4,"needed":4,"called":4,"succeeded":4,"failed":[],"unattempted":0,
         "quality_sent":"medium","quality_echoed":["medium"],"size":"1536x1024","model":"gpt-image-2",
         "usage":[],"relay_rejections":{"relay_prompt_unstyled":0,"relay_unknown_prompt":0,"relay_image_cap_reached":0,"relay_rate_limited":0}}
        """).RootElement.Clone();
}
