using FluentValidation;

namespace NightTales.Application.Features.Auth.Password.ForgotPassword.Commands;

/// <summary>
/// Shape validation of <c>POST /auth/forgot-password</c> (09.3.3-T1 Design), run by the D4
/// <c>ValidationBehavior</c> before the handler and the account lookup.
/// </summary>
/// <remarks>
/// It checks only what the wire fixes — the address's presence, its RFC 5321 length bound and its syntax —
/// so a malformed body is a 400 without touching the database. Whether the account exists is deliberately
/// not this validator's business: the handler answers the same 202 either way (BD16).
/// </remarks>
public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    /// <summary>The longest accepted e-mail address (RFC 5321 path limit), the same bound register uses.</summary>
    public const int EmailMaxLength = 254;

    public ForgotPasswordValidator()
    {
        RuleFor(command => command.Email)
            .NotEmpty().WithMessage("email_required")
            .MaximumLength(EmailMaxLength).WithMessage("email_too_long")
            .EmailAddress().WithMessage("email_invalid");
    }
}
