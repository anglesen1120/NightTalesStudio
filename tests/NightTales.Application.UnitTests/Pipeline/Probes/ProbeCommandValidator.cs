using FluentValidation;

namespace NightTales.Application.UnitTests.Pipeline.Probes;

/// <summary>Probe validator: records that validation ran, and fails <c>Name</c> / <c>Secret</c> on demand.</summary>
public sealed class ProbeCommandValidator : AbstractValidator<ProbeCommand>
{
    public ProbeCommandValidator(PipelineTrace trace)
    {
        RuleFor(command => command).Custom((_, _) => trace.Record("validate"));
        RuleFor(command => command.Name).NotEmpty().WithMessage("name_required");
        RuleFor(command => command.Secret).NotEqual(ProbeCommand.ForbiddenSecret).WithMessage("secret_forbidden");
    }
}
