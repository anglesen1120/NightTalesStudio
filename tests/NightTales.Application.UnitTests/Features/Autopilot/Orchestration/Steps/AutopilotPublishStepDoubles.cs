using ErrorOr;
using Mediator;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The read side of the render and publish steps over settable facts: each step decides on one row, so a test names
/// the row it wants the step to see and asserts the move, without a database that would only restate the query.
/// </summary>
/// <remarks>
/// Every method answers what it was given; a test that asserts "the step never read the channel" reads
/// <see cref="ChannelReads"/> instead of arranging a fake that throws, because the steps legitimately read the
/// episode first.
/// </remarks>
internal sealed class FakeStepReader : IAutopilotStepReader
{
    /// <summary>The episode the step reads, or <see langword="null"/> for a row outside the owner's scope.</summary>
    public AutopilotRenderEpisode? Episode { get; set; }

    /// <summary>The channel the run's authorization names, or <see langword="null"/> when none resolves.</summary>
    public AutopilotChannelSnapshot? Channel { get; set; }

    /// <summary>The series' current authorization, or <see langword="null"/> when the series has none.</summary>
    public Guid? CurrentAuthorizationId { get; set; }

    /// <summary>The publication the run attached.</summary>
    public AutopilotPublicationSnapshot? Publication { get; set; }

    /// <summary>The episode's newest live publication, when the create collided with it.</summary>
    public AutopilotActivePublication? ActivePublication { get; set; }

    /// <summary>The series' published_private_locked count (plan §3.6 row 14's N).</summary>
    public int PrivateLockedCount { get; set; }

    /// <summary>How many times the channel was resolved: the §3.6 triggers are asked only after the cut is approved.</summary>
    public int ChannelReads { get; private set; }

    /// <summary>The authorization ids the channel was resolved for, in order.</summary>
    public List<Guid?> ChannelAuthorizationIds { get; } = [];

    /// <inheritdoc />
    public ValueTask<AutopilotRenderEpisode?> FindEpisodeAsync(Guid episodeId, CancellationToken ct) =>
        new(Episode is { } episode && episode.Id == episodeId ? episode : null);

    /// <inheritdoc />
    public ValueTask<AutopilotChannelSnapshot?> FindChannelAsync(Guid? authorizationId, CancellationToken ct)
    {
        ChannelReads++;
        ChannelAuthorizationIds.Add(authorizationId);

        return new ValueTask<AutopilotChannelSnapshot?>(Channel);
    }

    /// <inheritdoc />
    public ValueTask<Guid?> FindCurrentAuthorizationAsync(SeriesAggregate series, CancellationToken ct) =>
        new(CurrentAuthorizationId);

    /// <inheritdoc />
    public ValueTask<AutopilotPublicationSnapshot?> FindPublicationAsync(Guid publicationId, CancellationToken ct) =>
        new(Publication is { } publication && publication.Id == publicationId ? publication : null);

    /// <inheritdoc />
    public ValueTask<AutopilotActivePublication?> FindActivePublicationAsync(Guid episodeId, CancellationToken ct) =>
        new(ActivePublication);

    /// <inheritdoc />
    public ValueTask<int> CountPrivateLockedRunsAsync(Guid seriesId, CancellationToken ct) => new(PrivateLockedCount);
}

/// <summary>
/// The 10 video checks, scripted: the step's four outcomes (pass, failed checks, a hold, an error) are each one
/// arrangement, and <see cref="Calls"/> proves a stored verdict was replayed instead of evaluated twice (S71 SEC-3).
/// </summary>
internal sealed class FakeVideoGate : IAutoApprovalGate
{
    /// <summary>The verdict the next evaluation answers; <see cref="ErrorOr{T}"/> carries the error case too.</summary>
    public ErrorOr<VideoGateOutcome> Verdict { get; set; } = Error.Unexpected("unset", "the test set no verdict");

    /// <summary>How many times the video half was evaluated.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public ValueTask<ErrorOr<StoryGateOutcome>> EvaluateStoryAsync(Guid runId, int attempt, CancellationToken ct) =>
        throw new NotSupportedException("the video-gate step never evaluates the story half");

    /// <inheritdoc />
    public ValueTask<ErrorOr<VideoGateOutcome>> EvaluateVideoAsync(Guid runId, CancellationToken ct)
    {
        Calls++;

        return new ValueTask<ErrorOr<VideoGateOutcome>>(Verdict);
    }
}

/// <summary>The consent the veto step waits for: "the row carries consent_at" is one boolean here.</summary>
internal sealed class FakeConsentGate(bool consented) : IAutopilotConsentGate
{
    /// <summary>Whether the window's consent was written.</summary>
    public bool Consented { get; set; } = consented;

    /// <summary>How many times the step asked.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public ValueTask<bool> IsConsentedAsync(
        AutopilotPublicationSnapshot publication,
        DateTimeOffset now,
        CancellationToken ct)
    {
        Calls++;

        return new ValueTask<bool>(Consented);
    }
}

/// <summary>The project-wide audit status as the pre-audit answer of every channel (06 README D11).</summary>
internal sealed class FakeChannelAuditPolicy(bool preAudit) : IChannelAuditPolicy
{
    /// <summary>Whether the channel is still unaudited.</summary>
    public bool PreAudit { get; set; } = preAudit;

    /// <inheritdoc />
    public ValueTask<bool> IsPreAuditAsync(Guid channelConnectionId, CancellationToken ct) => new(PreAudit);
}

/// <summary>
/// A run walked to a state through the machine's own legal chain, so a step test never writes a state the
/// transitions would refuse.
/// </summary>
internal static class AutopilotRuns
{
    /// <summary>The episode id every arranged run carries.</summary>
    public static readonly Guid EpisodeId = Guid.CreateVersion7();

    /// <summary>
    /// Plans a run and walks it to <paramref name="target"/> along the documented rows (plan §3.5), attaching the
    /// episode at <c>idea_picked</c> as the orchestrator does.
    /// </summary>
    /// <param name="series">The run's series.</param>
    /// <param name="target">The state the step under test handles.</param>
    /// <param name="now">The clock reading of every walk step.</param>
    /// <param name="episodeId">The episode the run carries; the step reads this id, so the fake must answer it.</param>
    /// <returns>The tracked run, in <paramref name="target"/>.</returns>
    public static AutopilotRun WalkTo(
        SeriesAggregate series,
        AutopilotRunState target,
        DateTimeOffset now,
        Guid? episodeId = null,
        Guid? publicationId = null,
        bool withPublication = true)
    {
        var run = AutopilotRun.Plan(
            series.UserId,
            series.Id,
            AutopilotStepContexts.DefaultSlotAt,
            startAt: now,
            authorizationId: null,
            now: now);

        run.AttachEpisode(episodeId ?? EpisodeId);

        if (withPublication && target is AutopilotRunState.PublicationCreated or AutopilotRunState.VetoWindow
            or AutopilotRunState.Scheduled or AutopilotRunState.Published or AutopilotRunState.Failed)
        {
            run.AttachPublication(publicationId ?? Guid.CreateVersion7());
        }

        foreach (var (state, step) in ChainTo(target))
        {
            run.AdvanceTo(state, step, now);
        }

        return run;
    }

    /// <summary>The legal chain from <c>planned</c> to the target, as the transitions the machine allows.</summary>
    private static IEnumerable<(AutopilotRunState State, string? Step)> ChainTo(AutopilotRunState target)
    {
        yield return (AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea);

        if (target == AutopilotRunState.IdeaPicked)
        {
            yield break;
        }

        yield return (AutopilotRunState.StoryGenerating, AutopilotRunStep.Story);

        if (target == AutopilotRunState.StoryGenerating)
        {
            yield break;
        }

        yield return (AutopilotRunState.StoryGate, AutopilotRunStep.StoryGate);

        if (target == AutopilotRunState.StoryGate)
        {
            yield break;
        }

        yield return (AutopilotRunState.Rendering, AutopilotRunStep.StoryGate);

        if (target == AutopilotRunState.Rendering)
        {
            yield break;
        }

        yield return (AutopilotRunState.VideoGate, AutopilotRunStep.Render);

        if (target == AutopilotRunState.VideoGate)
        {
            yield break;
        }

        // `waiting_owner` is skipped on purpose: entering it requires the waiting_for write that VideoGateStep makes
        // (`AdvanceTo` refuses the state otherwise), and plan §3.5 has the legal edge `video_gate →
        // publication_created` for the approved cut. A test that needs the wait arranges it through the step.
        if (target == AutopilotRunState.WaitingOwner)
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "walk the run to `video_gate` and let the step under test enter the wait");
        }

        yield return (AutopilotRunState.PublicationCreated, AutopilotRunStep.Publication);

        if (target == AutopilotRunState.PublicationCreated)
        {
            yield break;
        }

        // The veto window is entered with no step name: `PublicationCreatedStep.EnterVetoWindow` advances with
        // `step: null` (no command is sent), and the machine's row for `publication_created → veto_window` says the same.
        yield return (AutopilotRunState.VetoWindow, Step: null);

        if (target == AutopilotRunState.VetoWindow)
        {
            yield break;
        }

        yield return (AutopilotRunState.Scheduled, AutopilotRunStep.Consent);

        if (target == AutopilotRunState.Scheduled)
        {
            yield break;
        }

        throw new ArgumentOutOfRangeException(
            nameof(target),
            target,
            "the tests walk runs only through the documented chain; a terminal state is reached by the step under test");
    }
}

/// <summary>The wire text of a stored <c>gates.v1</c> document, for the video-gate replay cases (S71 SEC-3).</summary>
internal static class StoredGates
{
    /// <summary>A document whose video block records <paramref name="passed"/> under <paramref name="mode"/>.</summary>
    /// <param name="passed">The video block's <c>pass</c>.</param>
    /// <param name="mode">The document's mode, written with the writer's own wire name.</param>
    /// <returns>The JSON the run's <c>gate_results</c> holds.</returns>
    public static string Video(bool passed, GateMode mode) =>
        $$$"""
        {"{{{GateResultWriter.ModeKeyName}}}":"{{{mode.ToWire()}}}","{{{GateResultWriter.VideoKeyName}}}":{"pass":{{{(passed ? "true" : "false")}}}}}
        """;
}

/// <summary>
/// A sender for the publication create: it answers one <see cref="PublicationDto"/> (the creator reads the row's id)
/// or refuses with one fixed code, which is how the four §7.3 refusal mappings are arranged.
/// </summary>
/// <param name="publicationId">The id the created row answers with.</param>
/// <param name="episodeId">The episode the answered row belongs to.</param>
/// <param name="refusal">The error code every send answers with, or <see langword="null"/> for a created row.</param>
internal sealed class PublicationCreateSender(Guid publicationId, Guid episodeId, string? refusal = null) : ISender
{
    /// <summary>The id the create answers with.</summary>
    public Guid PublicationId { get; } = publicationId;

    /// <summary>Every message this sender was asked to send, in order.</summary>
    public List<object> Sent { get; } = [];

    /// <summary>Whether a message of <typeparamref name="T"/> was sent.</summary>
    /// <typeparam name="T">The message type the test looks for.</typeparam>
    /// <returns><see langword="true"/> when one was sent.</returns>
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

        if (refusal is { } code)
        {
            Error error = Error.Failure(code, code);

            return new ValueTask<TResponse>((TResponse)(dynamic)error);
        }

        ErrorOr<PublicationDto> created = new PublicationDto(
            PublicationId,
            episodeId,
            new PublicationChannelDto(Guid.CreateVersion7(), "a channel"),
            "awaiting_consent",
            "a title",
            "a description",
            [],
            "private",
            PreAudit: false,
            RequestedPrivacy: null,
            PublishAt: null,
            VetoEndsAt: null,
            ConsentAt: null,
            ConsentSource: null,
            ExternalPostId: null,
            ExternalUrl: null,
            ErrorCode: null,
            NextAttemptAt: null,
            Etag: "\"pu-1\"");

        return new ValueTask<TResponse>((TResponse)(dynamic)created);
    }
}
