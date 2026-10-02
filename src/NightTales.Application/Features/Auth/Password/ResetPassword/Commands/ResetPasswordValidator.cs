using FluentValidation;

namespace NightTales.Application.Features.Auth.Password.ResetPassword.Commands;

/// <summary>
/// Shape validation of <c>POST /auth/reset-password</c> (09.3.3-T1 Design), run by the D4
/// <c>ValidationBehavior</c> before the handler consumes anything.
/// </summary>
/// <remarks>
/// It checks only what the wire fixes: a present token and a present, bounded password. The password's
/// length and breach rules are <c>IPasswordPolicy</c>'s (09.3.1-T1), so a short password answers
/// <c>validation_failed</c> from the same checker the register flow uses rather than a second rule here.
/// </remarks>
public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    /// <summary>The longest accepted password input; Identity hashes whatever it is given.</summary>
    public const int PasswordMaxLength = 128;

    public ResetPasswordValidator()
    {
        RuleFor(command => command.Token)
            .NotEmpty().WithMessage("token_required");

        RuleFor(command => command.NewPassword)
            .NotEmpty().WithMessage("password_required")
            .MaximumLength(PasswordMaxLength).WithMessage("password_too_long");
    }
}
