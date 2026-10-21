using FluentValidation;

namespace NightTales.Application.Features.Research.Fetcher.Commands.HeartbeatFetchTask;

/// <summary>Shape validation of the heartbeat body (02.5.2-T2): a well-formed <c>workerId</c>.</summary>
public sealed class HeartbeatFetchTaskValidator : AbstractValidator<HeartbeatFetchTaskCommand>
{
    public HeartbeatFetchTaskValidator()
    {
        RuleFor(command => command.WorkerId).MustBeWorkerId();
    }
}
