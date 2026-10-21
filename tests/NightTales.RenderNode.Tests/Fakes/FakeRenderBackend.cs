using ErrorOr;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.RenderNode.Tests.Fakes;

/// <summary>
/// The scripted <see cref="IRenderBackend"/> of the core's tests: every call is counted, the submit answer and
/// the delete answer can be scripted to a failure, and an output download is built from the configured payload
/// so the test can assert the headers the core sends.
/// </summary>
internal sealed class FakeRenderBackend : IRenderBackend
{
    internal int SubmitCount { get; private set; }

    internal int CancelCount { get; private set; }

    internal int DeleteCount { get; private set; }

    internal int OpenOutputCount { get; private set; }

    internal MptSubmitFields? LastSubmittedFields { get; private set; }

    internal Guid? LastSubmittedTaskId { get; private set; }

    internal OutputKind? LastOutputKind { get; private set; }

    /// <summary>When set, <see cref="SubmitAsync"/> fails with it (the "backend refuses the submit" path).</summary>
    internal Error? SubmitError { get; set; }

    /// <summary>When set, <see cref="DeleteAsync"/> fails with it (<c>adapter_task_busy</c> while running).</summary>
    internal Error? DeleteError { get; set; }

    /// <summary>When set, <see cref="OpenOutputAsync"/> fails with it.</summary>
    internal Error? OpenOutputError { get; set; }

    internal byte[] VideoPayload { get; set; } = [1, 2, 3, 4];

    internal byte[] SubtitlesPayload { get; set; } = [5, 6, 7];

    internal string VideoSha256 { get; set; } = new('a', 64);

    internal string SubtitlesSha256 { get; set; } = new('b', 64);

    internal string BackendTaskId { get; set; } = "mpt-task-1";

    private readonly TaskCompletionSource _submitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once <see cref="SubmitAsync"/> was entered, so a test can act while it is in flight.</summary>
    internal Task SubmitEntered => _submitEntered.Task;

    /// <summary>When set, <see cref="SubmitAsync"/> waits for it before answering (a submit in flight).</summary>
    internal TaskCompletionSource? SubmitGate { get; set; }

    /// <summary>When true, <see cref="SubmitAsync"/> only ends when the request token is cancelled (a client abort).</summary>
    internal bool SubmitBlocksUntilCancelled { get; set; }

    /// <summary>The stream the last <see cref="OpenOutputAsync"/> handed out, for the disposal assertion.</summary>
    internal TrackingStream? LastOutputStream { get; private set; }

    /// <summary>When set, the last output stream fails on read (a copy failure).</summary>
    internal Exception? OutputStreamReadError { get; set; }

    /// <inheritdoc />
    public async Task<ErrorOr<BackendAccepted>> SubmitAsync(
        RenderTaskRecord record,
        MptSubmitFields mpt,
        CancellationToken cancellationToken)
    {
        SubmitCount++;
        LastSubmittedFields = mpt;
        LastSubmittedTaskId = record.TaskId;
        _submitEntered.TrySetResult();

        if (SubmitBlocksUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        if (SubmitGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        return SubmitError is { } error
            ? ErrorOrFactory.From<BackendAccepted>(error)
            : ErrorOrFactory.From(new BackendAccepted(BackendTaskId, record.SubmittedAt.AddSeconds(1)));
    }

    /// <inheritdoc />
    public Task CancelAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        CancelCount++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ErrorOr<Deleted>> DeleteAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        DeleteCount++;
        return Task.FromResult(DeleteError is { } error
            ? ErrorOrFactory.From<Deleted>(error)
            : ErrorOrFactory.From(default(Deleted)));
    }

    /// <inheritdoc />
    public Task<ErrorOr<OutputFile>> OpenOutputAsync(Guid taskId, OutputKind kind, CancellationToken cancellationToken)
    {
        OpenOutputCount++;
        LastOutputKind = kind;

        if (OpenOutputError is { } error)
        {
            return Task.FromResult(ErrorOrFactory.From<OutputFile>(error));
        }

        var payload = kind == OutputKind.Video ? VideoPayload : SubtitlesPayload;
        var sha256 = kind == OutputKind.Video ? VideoSha256 : SubtitlesSha256;
        var contentType = kind == OutputKind.Video ? RenderNodeHeaders.VideoContentType : RenderNodeHeaders.SubRipContentType;
        var stream = new TrackingStream(payload) { ReadError = OutputStreamReadError };
        LastOutputStream = stream;
        return Task.FromResult(ErrorOrFactory.From(new OutputFile(stream, payload.Length, sha256, contentType)));
    }

    private static class ErrorOrFactory
    {
        internal static ErrorOr<T> From<T>(T value) => value;

        internal static ErrorOr<T> From<T>(Error error) => error;
    }
}
