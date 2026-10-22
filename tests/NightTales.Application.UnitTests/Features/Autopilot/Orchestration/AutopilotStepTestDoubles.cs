using ErrorOr;
using Mediator;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration;

/// <summary>
/// A hand-written <see cref="ISender"/> for the step tests: it records every message, so a test asserts exactly which
/// commands a step sent (and which it did not) without a mediator pipeline, a database or a mocking library.
/// </summary>
/// <remarks>
/// Every send answers <see langword="default"/>, which is a <em>successful</em> <see cref="ErrorOr.ErrorOr{TValue}"/>
/// (no errors) — enough for the steps, which only read <c>IsError</c>. A test that needs an error answer passes its
/// own sender fake.
/// </remarks>
internal sealed class RecordingSender : ISender
{
    /// <summary>Every message this sender was asked to send, in order.</summary>
    public List<object> Sent { get; } = [];

    /// <summary>Whether a message of <typeparamref name="T"/> was sent.</summary>
    public bool SentAny<T>() => Sent.Exists(message => message is T);

    /// <summary>The single message sent, asserted to be <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The message type the test expects.</typeparam>
    /// <returns>The message.</returns>
    public T Single<T>() => Sent.ShouldHaveSingleItem().ShouldBeOfType<T>();

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Record<TResponse>(request);

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
        Record<TResponse>(command);

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) =>
        Record<TResponse>(query);

    /// <inheritdoc />
    public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);

        return new ValueTask<object?>((object?)null);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamQuery<TResponse> query,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamCommand<TResponse> command,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    private ValueTask<TResponse> Record<TResponse>(object message)
    {
        Sent.Add(message);

        return new ValueTask<TResponse>(default(TResponse)!);
    }
}

/// <summary>The <see cref="ISourceFinderGo"/> the step tests drive: the 02.7.1 "go" is a switch, not a flag read.</summary>
/// <param name="hasGo">What the port answers.</param>
internal sealed class FixedSourceFinderGo(bool hasGo) : ISourceFinderGo
{
    /// <summary>How many times the step asked; a no-go branch asks once, and a fact sheet wait asks not at all.</summary>
    public int Reads { get; private set; }

    /// <inheritdoc />
    public ValueTask<bool> HasGoAsync(CancellationToken ct)
    {
        Reads++;

        return ValueTask.FromResult(hasGo);
    }
}

/// <summary>
/// Builds the <see cref="AutopilotStepContext"/> of a test run from values the plan fixes, so a test states only the
/// run, the series and the instant it cares about.
/// </summary>
internal static class AutopilotStepContexts
{
    /// <summary>A UTC slot instant 24 h after <see cref="DefaultNow"/>.</summary>
    public static readonly DateTimeOffset DefaultSlotAt = new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

    /// <summary>The tick's clock reading the tests use unless they need an older run.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The platform timing options of the tests: the shipped defaults of <c>Autopilot:Run</c>.</summary>
    public static readonly AutopilotTimingOptions TimingOptions = new(
        PublishLeadMinutes: 120,
        MinVetoWindowHours: 2,
        PublicationCreateMarginMinutes: 10,
        RenderEstimateMinutes: 15,
        StoryEstimateMinutes: 10,
        MaxBufferDays: 7);

    /// <summary>The settings of a true-crime series at a twice-a-week cadence.</summary>
    /// <returns>A validated-shape settings record; the step never validates it again.</returns>
    public static AutopilotSettings Settings() => new(
        SchemaVersion: 1,
        Cadence: new AutopilotCadence(
            [DayOfWeek.Monday, DayOfWeek.Thursday],
            new TimeOnly(20, 0),
            "Asia/Ho_Chi_Minh"),
        DailyCap: 1,
        BufferDays: 2,
        Privacy: PrivacyStatus.Private,
        VetoWindowHours: 24,
        PreAuditMode: PreAuditMode.KeepCadencePrivate,
        PreAuditPauseAfter: 3,
        GateThresholdsRef: "default",
        Tone: "calm",
        BannedThemes: [],
        RenderLeadMinutes: null,
        StoryLeadMinutes: null);

    /// <summary>The context of one handler call at <paramref name="now"/>.</summary>
    /// <param name="run">The tracked run.</param>
    /// <param name="series">The run's series.</param>
    /// <param name="now">The tick's clock reading.</param>
    /// <returns>The context the handler reads.</returns>
    public static AutopilotStepContext For(AutopilotRun run, SeriesAggregate series, DateTimeOffset now)
    {
        var settings = Settings();

        return new AutopilotStepContext(
            run,
            series,
            settings,
            AutopilotTiming.Compute(run.SlotAt, settings, TimingOptions, now),
            TimingOptions,
            now);
    }
}

/// <summary>
/// A sender that records every message and refuses it with one fixed error code: the step paths that must stop or fail
/// on a catalogue error (the 03.2.2-T2 keyword lint, a 412) need a refusal, not a success.
/// </summary>
/// <param name="code">The error code every send answers with.</param>
internal sealed class RefusingSender(string code) : ISender
{
    /// <summary>Every message this sender was asked to send, in order.</summary>
    public List<object> Sent { get; } = [];

    /// <summary>The single message sent, asserted to be <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The message type the test expects.</typeparam>
    /// <returns>The message.</returns>
    public T Single<T>() => Sent.ShouldHaveSingleItem().ShouldBeOfType<T>();

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Record<TResponse>(request);

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
        Record<TResponse>(command);

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) =>
        Record<TResponse>(query);

    /// <inheritdoc />
    public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);

        return new ValueTask<object?>((object?)null);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamQuery<TResponse> query,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamCommand<TResponse> command,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    private ValueTask<TResponse> Record<TResponse>(object message)
    {
        Sent.Add(message);

        // Every command under test answers ErrorOr<T>, which converts from Error through its implicit operator.
        Error refusal = Error.Failure(code, code);

        return new ValueTask<TResponse>((TResponse)(dynamic)refusal);
    }
}
