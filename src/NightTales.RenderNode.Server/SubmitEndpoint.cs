using System.Security.Cryptography;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The submit pipeline of 05.4.1-T2 §3 in the frozen order: body cap, strict parse and validation, idempotency
/// by <c>render_job_id</c> + raw-body sha256, health, the capacity gate, then the image plan, the backend submit
/// and the answer.
/// </summary>
/// <remarks>
/// Health is checked before the gate on purpose: the acceptance rule is that an unhealthy node refuses a submit
/// with <c>503 adapter_node_unhealthy</c> and leaves the store untouched, and a row must not be inserted (and
/// rolled back) to learn that. The only observable difference to §3's wording is a node that is both unhealthy
/// and full, which answers 503 instead of 429.
/// The §3 idempotency decision is race-safe against capacity: the pre-gate lookup answers the replay/conflict for
/// a row that is already visible, and <see cref="CapacityGate.TryEnterAsync"/> re-checks the <c>render_job_id</c>
/// inside its critical section for the in-flight duplicate. Two identical submits at one free slot therefore give
/// <c>202</c> + <c>200</c> (never <c>429</c>), and a same-id different-body pair gives <c>409</c>.
/// </remarks>
internal static class SubmitEndpoint
{
    /// <summary>
    /// The closed detail of a <c>render_node_error</c> the core raises itself when the backend refuses a submit
    /// that was already stored: the node failed before media generation, which is the <c>pipeline</c> stage of
    /// 05.4.1-T2 §4's <c>failed_stage</c> set (never free text, §10 T63).
    /// </summary>
    private const string SubmitFailureDetail = "pipeline";

    internal static async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.RequestServices;
        var cancellationToken = context.RequestAborted;
        var options = services.GetRequiredService<IOptions<RenderNodeServerOptions>>().Value;
        var store = services.GetRequiredService<ITaskStore>();
        var gate = services.GetRequiredService<CapacityGate>();
        var backend = services.GetRequiredService<IRenderBackend>();
        var planSink = services.GetService<IImagePlanSink>();

        using var body = await RequestBodyReader
            .ReadAsync(context.Request, options.MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        if (body is null)
        {
            await RenderNodeEnvelope
                .WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, AdapterErrorCodes.BodyTooLarge)
                .ConfigureAwait(false);
            return;
        }

        var validated = SubmitValidator.Validate(body.Memory.Span);
        if (validated.IsError)
        {
            await RenderNodeEnvelope
                .WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    AdapterErrorCodes.ContractViolation,
                    ToFieldErrors(validated.Errors))
                .ConfigureAwait(false);
            return;
        }

        var submit = validated.Value;
        var bodySha256 = Convert.ToHexStringLower(SHA256.HashData(body.Memory.Span));

        // The idempotency decision of §3 comes first, before health and capacity: the same job id with the same
        // body hash is the replay (200), the same job id with another body is the conflict (409).
        var existing = await store
            .FindByRenderJobIdAsync(submit.RenderJobId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.BodySha256, bodySha256, StringComparison.Ordinal))
            {
                await RenderNodeEnvelope
                    .WriteErrorAsync(context, StatusCodes.Status409Conflict, AdapterErrorCodes.IdempotencyConflict)
                    .ConfigureAwait(false);
                return;
            }

            await WriteAcceptedAsync(context, StatusCodes.Status200OK, existing).ConfigureAwait(false);
            return;
        }

        var (healthState, _) = await NodeHealth.ReadAsync(services, cancellationToken).ConfigureAwait(false);
        if (healthState is not NodeHealthState.Ok)
        {
            await RenderNodeEnvelope
                .WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, AdapterErrorCodes.NodeUnhealthy)
                .ConfigureAwait(false);
            return;
        }

        var now = Clock(services).GetUtcNow();
        var candidate = new RenderTaskRecord(
            TaskId: Guid.CreateVersion7(now),
            RenderJobId: submit.RenderJobId,
            BodySha256: bodySha256,
            BackendTaskId: null,
            SubmittedAt: now,
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

        var entered = await gate.TryEnterAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (entered.IsError)
        {
            await WriteGateErrorAsync(context, entered.FirstError, store, submit.RenderJobId, bodySha256, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var record = entered.Value;
        planSink?.Register(record.TaskId, submit.ImagePlan);

        ErrorOr<BackendAccepted> accepted;
        try
        {
            accepted = await backend.SubmitAsync(record, submit.Mpt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            // The client went away while the backend was still accepting the job: the outcome is unknown, so
            // the row must not stay non-terminal (it would hold the node's slot and make a replay answer a
            // lie). Finish it on a token the aborted request cannot cancel.
            await FinishRejectedSubmitAsync(store, planSink, record, Clock(services).GetUtcNow()).ConfigureAwait(false);
            return;
        }
        catch
        {
            // An unexpected fault of the backend leaves the same orphan as an abort; finish the row, then let
            // the exception middleware answer.
            await FinishRejectedSubmitAsync(store, planSink, record, Clock(services).GetUtcNow()).ConfigureAwait(false);
            throw;
        }

        if (accepted.IsError)
        {
            await FinishRejectedSubmitAsync(store, planSink, record, Clock(services).GetUtcNow()).ConfigureAwait(false);

            await RenderNodeEnvelope
                .WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, AdapterErrorCodes.NodeUnhealthy)
                .ConfigureAwait(false);
            return;
        }

        // The acceptance is recorded on the current row, not on the pre-submit snapshot, so a cancel accepted
        // while the backend was submitting is not overwritten (11.3.5-T1 Failure handling).
        _ = await store
            .RecordBackendAcceptanceAsync(record.TaskId, accepted.Value.BackendTaskId, accepted.Value.StartedAt, CancellationToken.None)
            .ConfigureAwait(false);

        var submitted = record with
        {
            BackendTaskId = accepted.Value.BackendTaskId,
            BackendStartedAt = accepted.Value.StartedAt,
        };
        await WriteAcceptedAsync(context, StatusCodes.Status202Accepted, submitted).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminalises a stored-but-not-accepted task: releases the image plan and records the same
    /// <c>failed</c>/<c>render_node_error</c>/<c>pipeline</c> row the backend-refusal path writes, so no orphan
    /// row keeps the node's capacity. The store write uses <see cref="CancellationToken.None"/> on purpose: it
    /// must still run when the reason for the cleanup was a client abort.
    /// </summary>
    private static async Task<bool> FinishRejectedSubmitAsync(
        ITaskStore store,
        IImagePlanSink? planSink,
        RenderTaskRecord record,
        DateTimeOffset finishedAt)
    {
        planSink?.Release(record.TaskId);

        // `record` is the pre-submit snapshot, so its CancelRequested is always false. A `/cancel` accepted
        // while backend.SubmitAsync was in flight has already set the stored flag, and Merge is last-writer-wins
        // on it (RenderTaskRecord.Merge) — carrying the snapshot would silently reset an accepted cancel on
        // this failed row. Re-read the live row and preserve its flag; like the write below, the read uses a
        // token the aborted request cannot cancel.
        var stored = await store.TryGetAsync(record.TaskId, CancellationToken.None).ConfigureAwait(false);
        return await TaskUpdates
            .UpdateAsync(
                store,
                record with
                {
                    State = RenderStates.Failed,
                    CancelRequested = stored?.CancelRequested ?? record.CancelRequested,
                    Outcome = new RenderOutcomeV1(
                        RenderOutcomeCodes.RenderNodeError,
                        SubmitFailureDetail,
                        OutcomeMessages.For(RenderOutcomeCodes.RenderNodeError)),
                    FinishedAt = finishedAt,
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task WriteGateErrorAsync(
        HttpContext context,
        Error error,
        ITaskStore store,
        Guid renderJobId,
        string bodySha256,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(error.Code, AdapterErrorCodes.IdempotencyConflict, StringComparison.Ordinal))
        {
            await AdapterErrorStatus
                .WriteAsync(context, error, retryAfterSeconds: CapacityRetryAfterSeconds)
                .ConfigureAwait(false);
            return;
        }

        // The row appeared between the replay lookup and the insert (two submits of the same job id): a
        // byte-identical one is still a replay, anything else is the conflict of §3.
        var raced = await store
            .FindByRenderJobIdAsync(renderJobId, cancellationToken)
            .ConfigureAwait(false);
        if (raced is not null && string.Equals(raced.BodySha256, bodySha256, StringComparison.Ordinal))
        {
            await WriteAcceptedAsync(context, StatusCodes.Status200OK, raced).ConfigureAwait(false);
            return;
        }

        await RenderNodeEnvelope
            .WriteErrorAsync(context, StatusCodes.Status409Conflict, AdapterErrorCodes.IdempotencyConflict)
            .ConfigureAwait(false);
    }

    private static Task WriteAcceptedAsync(HttpContext context, int statusCode, RenderTaskRecord record) =>
        RenderNodeEnvelope.WriteJsonAsync(context, statusCode, new AdapterAcceptedV1(record.TaskId, record.State));

    private static List<AdapterFieldErrorV1> ToFieldErrors(List<Error> errors)
    {
        var fieldErrors = new List<AdapterFieldErrorV1>(errors.Count);
        foreach (var error in errors)
        {
            // The validator already redacted the field (AdapterFieldNames) and put it in the description.
            fieldErrors.Add(new AdapterFieldErrorV1(error.Description, error.Code));
        }

        return fieldErrors;
    }

    internal const int CapacityRetryAfterSeconds = 30;

    internal static TimeProvider Clock(IServiceProvider services) =>
        services.GetService<TimeProvider>() ?? TimeProvider.System;
}
