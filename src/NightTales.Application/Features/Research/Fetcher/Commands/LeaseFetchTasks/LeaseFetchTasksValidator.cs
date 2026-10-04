using FluentValidation;

namespace NightTales.Application.Features.Research.Fetcher.Commands.LeaseFetchTasks;

/// <summary>
/// Shape validation of the lease body (02.5.2-T2): <c>max</c> in [1, 4], and a well-formed <c>workerId</c>.
/// </summary>
public sealed class LeaseFetchTasksValidator : AbstractValidator<LeaseFetchTasksCommand>
{
    public LeaseFetchTasksValidator()
    {
        RuleFor(command => command.WorkerId).MustBeWorkerId();
        RuleFor(command => command.Max).InclusiveBetween(1, LeaseFetchTasksCommand.MaxBatch);
    }
}
