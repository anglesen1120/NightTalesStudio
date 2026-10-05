using FluentValidation;

namespace NightTales.Application.Features.Account.Legal.Commands.AcceptLegalDocuments;

/// <summary>Both document versions are required, bounded, and semver-like.</summary>
public sealed class AcceptLegalDocumentsValidator : AbstractValidator<AcceptLegalDocumentsCommand>
{
    /// <summary>Defines the public version shape, without validating client equality here.</summary>
    public AcceptLegalDocumentsValidator()
    {
        RuleFor(command => command.Tos).NotEmpty().MaximumLength(32).Matches(@"^\d+\.\d+\.\d+\z");
        RuleFor(command => command.Privacy).NotEmpty().MaximumLength(32).Matches(@"^\d+\.\d+\.\d+\z");
    }
}
