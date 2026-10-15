using ErrorOr;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Infrastructure.Rendering;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// The adapter-error mapping of 09.6.2-T1 (09 README BD6, 05 README D4/D5) and the failure round trip
/// 09.6.3 consumes: every row of the adapter table, the <c>Retry-After</c> rules, the two "not a failure"
/// kinds and the option validator's start-up gate.
/// </summary>
public sealed class AdapterErrorMapperTests
{
    /// <summary>
    /// The full table: status, envelope code, whether the task had been submitted, and the expected failure.
    /// <c>-1</c> in the last column means "no retry-after"; every other value is a whole number of seconds.
    /// </summary>
    [Theory]
    [InlineData(429, AdapterErrorCodes.CapacityFull, false, RenderClientFailureKind.Deferred, null, false, 30)]
    [InlineData(503, AdapterErrorCodes.NodeUnhealthy, false, RenderClientFailureKind.Deferred, null, false, 30)]
    [InlineData(404, AdapterErrorCodes.TaskUnknown, true, RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderLost, false, -1)]
    [InlineData(404, AdapterErrorCodes.TaskUnknown, false, RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderNodeError, true, -1)]
    [InlineData(409, AdapterErrorCodes.OutputNotReady, false, RenderClientFailureKind.OutputNotReady, null, false, -1)]
    [InlineData(404, AdapterErrorCodes.OutputAbsent, false, RenderClientFailureKind.OutputAbsent, null, false, -1)]
    [InlineData(409, AdapterErrorCodes.TaskBusy, false, RenderClientFailureKind.Deferred, null, false, 30)]
    [InlineData(400, AdapterErrorCodes.ContractViolation, false, RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderNodeError, true, -1)]
    [InlineData(409, AdapterErrorCodes.IdempotencyConflict, false, RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderNodeError, true, -1)]
    [InlineData(413, AdapterErrorCodes.BodyTooLarge, false, RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderNodeError, true, -1)]
    [InlineData(401, AdapterErrorCodes.Unauthorized, false, RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderNodeError, true, -1)]
    [InlineData(404, AdapterErrorCodes.RouteNotFound, false, RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderNodeError, true, -1)]
    [InlineData(500, AdapterErrorCodes.InternalError, false, RenderClientFailureKind.TransientFailure, RenderOutcomeCodes.RenderNodeError, false, -1)]
    [InlineData(500, null, false, RenderClientFailureKind.TransientFailure, RenderOutcomeCodes.RenderNodeError, false, -1)]
    [InlineData(503, null, false, RenderClientFailureKind.TransientFailure, RenderOutcomeCodes.RenderNodeError, false, -1)]
    [InlineData(302, null, false, RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderNodeError, true, -1)]
    [InlineData(429, null, false, RenderClientFailureKind.Deferred, null, false, 30)]
    [InlineData(200, null, false, RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderNodeError, true, -1)]
    public void Map_FullAdapterCodeTable(
        int status,
        string? code,
        bool taskSubmitted,
        RenderClientFailureKind kind,
        string? outcomeCode,
        bool alert,
        int retryAfterSeconds)
    {
        var expected = new RenderClientFailure(
            kind,
            outcomeCode,
            Detail: null,
            Alert: alert,
            RetryAfter: retryAfterSeconds < 0 ? null : TimeSpan.FromSeconds(retryAfterSeconds));

        AdapterErrorMapper
            .Map(RenderClientError.Status(status, code), taskSubmitted)
            .ShouldBe(expected);
    }

    [Fact]
    public void Map_TransportError_IsTransientNodeError()
    {
        AdapterErrorMapper
            .Map(RenderClientError.Transport(), taskSubmitted: true)
            .ShouldBe(new RenderClientFailure(
                RenderClientFailureKind.TransientFailure, RenderOutcomeCodes.RenderNodeError, Detail: null, Alert: false, RetryAfter: null));
    }

    [Fact]
    public void Map_Timeout_IsTransientNodeError()
    {
        AdapterErrorMapper
            .Map(RenderClientError.Timeout(), taskSubmitted: true)
            .ShouldBe(new RenderClientFailure(
                RenderClientFailureKind.TransientFailure, RenderOutcomeCodes.RenderNodeError, Detail: null, Alert: false, RetryAfter: null));
    }

    [Fact]
    public void Map_UnknownCode_IsTerminalNodeError_WithAlert()
    {
        var failure = AdapterErrorMapper.Map(
            RenderClientError.Status(400, "adapter_surprise"),
            taskSubmitted: false);

        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Detail.ShouldBe("adapter_surprise");
        failure.Alert.ShouldBeTrue();
        failure.RetryAfter.ShouldBeNull();

        // The adapter's code is the only thing that survives, and it never reaches the message itself.
        var error = RenderClientErrors.ToError(failure);
        error.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        error.Description.ShouldBe(OutcomeMessages.For(RenderOutcomeCodes.RenderNodeError));
        error.Description.ShouldNotContain("adapter_surprise");
    }

    [Fact]
    public void Map_TaskUnknown_OnSubmittedTask_IsRenderLost()
    {
        var failure = AdapterErrorMapper.Map(
            RenderClientError.Status(404, AdapterErrorCodes.TaskUnknown),
            taskSubmitted: true);

        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderLost);
        failure.Detail.ShouldBeNull();
        failure.Alert.ShouldBeFalse();
        failure.RetryAfter.ShouldBeNull();
    }

    [Fact]
    public void Map_TaskUnknown_OnUnsubmittedTask_IsNodeErrorWithAlert()
    {
        var failure = AdapterErrorMapper.Map(
            RenderClientError.Status(404, AdapterErrorCodes.TaskUnknown),
            taskSubmitted: false);

        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeTrue();
    }

    [Fact]
    public void Map_OutputAbsent_IsNotAnError()
    {
        var failure = AdapterErrorMapper.Map(
            RenderClientError.Status(404, AdapterErrorCodes.OutputAbsent),
            taskSubmitted: true);

        failure.Kind.ShouldBe(RenderClientFailureKind.OutputAbsent);
        failure.OutcomeCode.ShouldBeNull();
        failure.Detail.ShouldBeNull();
        failure.Alert.ShouldBeFalse();
        failure.RetryAfter.ShouldBeNull();

        // The call does fail, and the kind is the only thing that distinguishes "write nothing" from a real
        // download fault: this is how a caller detects the absence of a subtitle track (05 D10).
        var error = RenderClientErrors.ToError(failure);
        error.Code.ShouldBe(nameof(RenderClientFailureKind.OutputAbsent));
        RenderClientErrors.TryGetFailure(error, out var recovered).ShouldBeTrue();
        recovered.Kind.ShouldBe(RenderClientFailureKind.OutputAbsent);
    }

    [Fact]
    public void Map_OutputNotReady_IsRetriedByTheIngest()
    {
        var failure = AdapterErrorMapper.Map(
            RenderClientError.Status(409, AdapterErrorCodes.OutputNotReady),
            taskSubmitted: true);

        failure.Kind.ShouldBe(RenderClientFailureKind.OutputNotReady);
        failure.OutcomeCode.ShouldBeNull();
        failure.Alert.ShouldBeFalse();
        failure.RetryAfter.ShouldBeNull();
    }

    [Fact]
    public void Map_CapacityFull_ReturnsRetryAfterFromHeader()
    {
        var failure = AdapterErrorMapper.Map(
            RenderClientError.Status(429, AdapterErrorCodes.CapacityFull, retryAfter: TimeSpan.FromSeconds(45)),
            taskSubmitted: false);

        failure.Kind.ShouldBe(RenderClientFailureKind.Deferred);
        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(45));
        failure.Alert.ShouldBeFalse();
        failure.OutcomeCode.ShouldBeNull();
    }

    [Fact]
    public void Map_CapacityFull_WithoutHeader_DefersThirtySeconds()
    {
        AdapterErrorMapper
            .Map(RenderClientError.Status(429, AdapterErrorCodes.CapacityFull), taskSubmitted: false)
            .RetryAfter
            .ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Map_CapacityFull_WithExcessiveHeader_IsClampedToFiveMinutes()
    {
        AdapterErrorMapper
            .Map(
                RenderClientError.Status(429, AdapterErrorCodes.CapacityFull, retryAfter: TimeSpan.FromSeconds(900)),
                taskSubmitted: false)
            .RetryAfter
            .ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void Map_CapacityFull_WithNegativeHeader_IsClampedToZero()
    {
        AdapterErrorMapper
            .Map(
                RenderClientError.Status(429, AdapterErrorCodes.CapacityFull, retryAfter: TimeSpan.FromSeconds(-5)),
                taskSubmitted: false)
            .RetryAfter
            .ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Map_NodeUnhealthy_DefersTheConfiguredHealthPoll()
    {
        AdapterErrorMapper
            .Map(
                RenderClientError.Status(503, AdapterErrorCodes.NodeUnhealthy),
                taskSubmitted: false,
                healthPoll: TimeSpan.FromSeconds(12))
            .RetryAfter
            .ShouldBe(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void Map_TaskBusy_DefersThirtySeconds()
    {
        AdapterErrorMapper
            .Map(RenderClientError.Status(409, AdapterErrorCodes.TaskBusy), taskSubmitted: true)
            .RetryAfter
            .ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void OutputInvalid_IsTerminalOutputInvalidWithoutAlert()
    {
        var failure = AdapterErrorMapper.OutputInvalid("video_bytes_exceeded");

        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
        failure.Detail.ShouldBe("video_bytes_exceeded");
        failure.Alert.ShouldBeFalse();
        failure.RetryAfter.ShouldBeNull();
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData(" 45 ", 45)]
    [InlineData("0", 0)]
    [InlineData("300", 300)]
    [InlineData(null, -1)]
    [InlineData("", -1)]
    [InlineData("   ", -1)]
    [InlineData("soon", -1)]
    [InlineData("-5", -1)]
    [InlineData("12.5", -1)]
    [InlineData("Wed, 21 Oct 2015 07:28:00 GMT", -1)]
    public void ParseRetryAfter_AcceptsIntegerSecondsOnly(string? header, int expectedSeconds)
    {
        var parsed = AdapterErrorMapper.ParseRetryAfter(header);

        // A usable header is that many seconds; -1 in this table means "no usable value", which must never
        // become a zero wait (09.6.2-T1 task §Failure handling).
        if (expectedSeconds < 0)
        {
            parsed.ShouldBeNull();
        }
        else
        {
            parsed.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
        }
    }

    [Fact]
    public void RenderClientErrors_TerminalFailure_RoundTripsAsFailure()
    {
        var failure = new RenderClientFailure(
            RenderClientFailureKind.TerminalFailure,
            RenderOutcomeCodes.RenderLost,
            Detail: "adapter_task_unknown",
            Alert: false,
            RetryAfter: TimeSpan.FromSeconds(7));

        var error = RenderClientErrors.ToError(failure);

        error.Type.ShouldBe(ErrorType.Failure);
        error.Code.ShouldBe(RenderOutcomeCodes.RenderLost);
        error.Description.ShouldBe("The render task is no longer known to the node (MPT restart or lost task state).");
        RenderClientErrors.TryGetFailure(error, out var recovered).ShouldBeTrue();
        recovered.ShouldBe(failure);
    }

    [Fact]
    public void RenderClientErrors_DeferredFailure_RoundTripsAsUnexpectedWithTheKindName()
    {
        var failure = new RenderClientFailure(
            RenderClientFailureKind.Deferred,
            OutcomeCode: null,
            Detail: null,
            Alert: false,
            RetryAfter: TimeSpan.FromSeconds(30));

        var error = RenderClientErrors.ToError(failure);

        error.Type.ShouldBe(ErrorType.Unexpected);
        error.Code.ShouldBe(nameof(RenderClientFailureKind.Deferred));
        error.Description.ShouldBe("The render node cannot take the job right now; it is offered again later.");
        RenderClientErrors.TryGetFailure(error, out var recovered).ShouldBeTrue();
        recovered.ShouldBe(failure);
    }

    [Fact]
    public void RenderClientErrors_TryGetFailure_IsFalseForAnotherError()
    {
        RenderClientErrors.TryGetFailure(Error.Failure("render_subject_invalid", "The subject is required."), out var failure)
            .ShouldBeFalse();
        failure.ShouldBeNull();
    }

    [Fact]
    public void RenderClientOptionsValidator_AcceptsTheDefaults()
    {
        new RenderClientOptionsValidator().Validate(null, new RenderClientOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void RenderClientOptionsValidator_AcceptsZeroRetries()
    {
        var options = new RenderClientOptions { Client = new() { MaxRetries = 0 } };

        new RenderClientOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void RenderClientOptionsValidator_RejectsUnusableValues()
    {
        // The validator's start-up gate, one row per key: a timeout, a cap or a breaker number that is zero
        // (or, for a count, negative) can never be honoured at run time (09 README BD6/W9).
        (RenderClientOptions Options, string Expected)[] cases =
        [
            (new RenderClientOptions { Client = new() { AttemptSeconds = 0 } }, "'Render:Client:AttemptSeconds' must be greater than zero."),
            (new RenderClientOptions { Client = new() { AttemptSeconds = -1 } }, "'Render:Client:AttemptSeconds' must be greater than zero."),
            (new RenderClientOptions { Client = new() { TotalSeconds = 0 } }, "'Render:Client:TotalSeconds' must be greater than zero."),
            (new RenderClientOptions { Client = new() { MaxRetries = -1 } }, "'Render:Client:MaxRetries' cannot be negative."),
            (new RenderClientOptions { Client = new() { CircuitBreaker = new() { MinimumThroughput = 0 } } }, "'Render:Client:CircuitBreaker:MinimumThroughput' must be greater than zero."),
            (new RenderClientOptions { Client = new() { CircuitBreaker = new() { FailureRatio = 0 } } }, "'Render:Client:CircuitBreaker:FailureRatio' must be greater than zero and at most 1."),
            (new RenderClientOptions { Client = new() { CircuitBreaker = new() { FailureRatio = 1.5 } } }, "'Render:Client:CircuitBreaker:FailureRatio' must be greater than zero and at most 1."),
            (new RenderClientOptions { Client = new() { CircuitBreaker = new() { SamplingDurationSeconds = 0 } } }, "'Render:Client:CircuitBreaker:SamplingDurationSeconds' must be greater than zero."),
            (new RenderClientOptions { Client = new() { CircuitBreaker = new() { BreakDurationSeconds = -30 } } }, "'Render:Client:CircuitBreaker:BreakDurationSeconds' must be greater than zero."),
            (new RenderClientOptions { Download = new() { IdleSeconds = 0 } }, "'Render:Download:IdleSeconds' must be greater than zero."),
            (new RenderClientOptions { DownloadMaxMinutes = 0 }, "'Render:DownloadMaxMinutes' must be greater than zero."),
            (new RenderClientOptions { MaxVideoBytes = 0 }, "'Render:MaxVideoBytes' must be greater than zero."),
            (new RenderClientOptions { MaxSubtitleBytes = -1 }, "'Render:MaxSubtitleBytes' must be greater than zero."),
        ];

        foreach (var (options, expected) in cases)
        {
            var result = new RenderClientOptionsValidator().Validate(null, options);

            result.Failed.ShouldBeTrue($"the validator must reject the case reported as {expected}");
            result.FailureMessage.ShouldContain(expected);
        }
    }
}
