using FluentValidation;

namespace NightTales.Application.Features.Publishing.Commands.ConsentPublication;

/// <summary>
/// The shape check of the consent body (06.2.2-T1 task §Steps 1, 06 README D7).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> the text-version comparison: whether <see cref="ConsentPublicationCommand.ConsentTextVersion"/>
/// is the version the host currently holds is the domain's rule (<c>Publication.Consent</c>), which answers the 400
/// <c>consent_text_outdated</c> carrying the request field the client re-reads, so the two checks cannot drift and a
/// stale version is never reported as a malformed body.
/// </para>
/// <para>
/// What is left is load-bearing: a JSON body may omit the version or set it to <c>null</c>, and the bound maximum
/// keeps a body that could never be a version out of the handler (the version is a short frozen token such as
/// <c>pc.v1</c>). An empty <see cref="ConsentPublicationCommand.PublicationId"/> is refused here rather than looked
/// up, because it names no row and the task's validation states the rule.
/// </para>
/// </remarks>
public sealed class ConsentPublicationValidator : AbstractValidator<ConsentPublicationCommand>
{
    /// <summary>The longest consent-text version the body may carry (the task's <c>≤ 32</c> rule).</summary>
    public const int ConsentTextVersionMaxLength = 32;

    /// <summary>Builds the shape rules.</summary>
    public ConsentPublicationValidator()
    {
        RuleFor(command => command.PublicationId)
            .NotEmpty()
            .WithMessage("A publication id is required.");

        RuleFor(command => command.ConsentTextVersion)
            .NotEmpty()
            .WithMessage("A consent text version is required.")
            .MaximumLength(ConsentTextVersionMaxLength)
            .WithMessage($"A consent text version is at most {ConsentTextVersionMaxLength} characters.");
    }
}
