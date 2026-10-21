using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect;
using NightTales.TestKit.Api;

namespace NightTales.TestKit.Publishing;

/// <summary>
/// A hand-written stand-in for <c>CompleteYouTubeConnectHandler</c>: it answers one <see cref="ErrorOr{T}"/>
/// error instead of running the callback, so the endpoint's envelope split — the arm that maps the claim's
/// own unit failures (<c>oauth_state_invalid</c>, <c>concurrency_conflict</c>) to
/// <c>?error=oauth_state_invalid</c> and every other code to <c>?error=oauth_exchange_failed</c> — is
/// exercised for real, through the real pipeline, with no internals visibility.
/// </summary>
/// <remarks>
/// The command interface is the seam slice D verified in Mediator's generated registration: the handler is
/// registered under <see cref="ICommandHandler{TCommand,TResponse}"/>, appended, and the dispatch wrapper
/// resolves it from the provider by that interface at call time, so the descriptor registered last wins.
/// Nothing else is replaced — the logging, authorization, validation, unit-of-work and audit behaviours of
/// the pipeline still run, which is the point: they are what turns an error into the endpoint's constant.
/// </remarks>
/// <param name="answer">What the callback's handler answers; an <see cref="Error"/> for the envelope cases.</param>
public sealed class FakeCompleteYouTubeConnectHandler(ErrorOr<ConnectYouTubeOutcome> answer)
    : ICommandHandler<CompleteYouTubeConnectCommand, ErrorOr<ConnectYouTubeOutcome>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<ConnectYouTubeOutcome>> Handle(
        CompleteYouTubeConnectCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ValueTask.FromResult(answer);
    }
}

/// <summary>The registration seam of <see cref="FakeCompleteYouTubeConnectHandler"/>: the test host only.</summary>
public static class FakeCompleteYouTubeConnectHosting
{
    /// <summary>
    /// Registers <see cref="FakeCompleteYouTubeConnectHandler"/> for <paramref name="answer"/> as the last
    /// <see cref="ICommandHandler{TCommand,TResponse}"/> descriptor, so the callback answers
    /// <paramref name="answer"/> instead of exchanging the code.
    /// </summary>
    /// <param name="factory">The factory to configure.</param>
    /// <param name="answer">The outcome or error the fake answers.</param>
    public static ApiFactory WithFakeCompleteYouTubeConnectHandler(
        this ApiFactory factory,
        ErrorOr<ConnectYouTubeOutcome> answer)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return factory.WithServices(services =>
        {
            services.RemoveAll<ICommandHandler<CompleteYouTubeConnectCommand, ErrorOr<ConnectYouTubeOutcome>>>();
            services.AddScoped<ICommandHandler<CompleteYouTubeConnectCommand, ErrorOr<ConnectYouTubeOutcome>>>(
                _ => new FakeCompleteYouTubeConnectHandler(answer));
        });
    }
}
