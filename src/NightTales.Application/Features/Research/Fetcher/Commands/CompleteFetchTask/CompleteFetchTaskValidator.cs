using FluentValidation;

namespace NightTales.Application.Features.Research.Fetcher.Commands.CompleteFetchTask;

/// <summary>
/// Shape validation of the result body (02.5.2-T2): only the <c>workerId</c>. Everything else is checked by
/// <see cref="FetchTaskResultValidator"/> <em>after</em> the lease ownership check, because the contract orders them:
/// a worker that lost its lease gets <c>409 fetch_lease_lost</c>, whatever its body looks like.
/// </summary>
public sealed class CompleteFetchTaskValidator : AbstractValidator<CompleteFetchTaskCommand>
{
    public CompleteFetchTaskValidator()
    {
        RuleFor(command => command.Body).NotNull();
        RuleFor(command => command.Body.WorkerId).MustBeWorkerId().When(command => command.Body is not null);
    }
}
