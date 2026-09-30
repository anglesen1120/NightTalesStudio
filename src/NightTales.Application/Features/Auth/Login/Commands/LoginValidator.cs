using FluentValidation;

namespace NightTales.Application.Features.Auth.Login.Commands;

/// <summary>
/// Shape validation of <c>POST /auth/login</c> (09.3.2-T1 Design), run by the D4
/// <c>ValidationBehavior</c> <em>before</em> the handler and the credential check, so a malformed body
/// answers 400 <c>validation_failed</c> and can never become an enumeration oracle.
/// </summary>
/// <remarks>
/// The two bounds mirror 09.3.1-T1's <c>RegisterValidator</c>: an address longer than RFC 5321's path
/// limit and a password longer than the value Identity hashes are shape errors, not credential failures.
/// Whether the password is <em>right</em> is the credential verifier's answer, never this validator's.
/// </remarks>
public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    /// <summary>The longest accepted e-mail address (RFC 5321 path limit), as registration uses.</summary>
    public const int EmailMaxLength = 254;

    /// <summary>The longest accepted password input; Identity hashes whatever it is given.</summary>
    public const int PasswordMaxLength = 128;

    public LoginValidator()
    {
        RuleFor(command => command.Email)
            .NotEmpty().WithMessage("email_required")
            .MaximumLength(EmailMaxLength).WithMessage("email_too_long")
            .EmailAddress().WithMessage("email_invalid");

        RuleFor(command => command.Password)
            .NotEmpty().WithMessage("password_required")
            .MaximumLength(PasswordMaxLength).WithMessage("password_too_long");
    }
}
