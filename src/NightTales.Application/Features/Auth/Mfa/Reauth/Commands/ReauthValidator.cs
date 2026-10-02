using FluentValidation;

namespace NightTales.Application.Features.Auth.Mfa.Reauth.Commands;

/// <summary>
/// Shape validation of <c>POST /auth/reauth</c> (09.10.4-T1 Design): the password is required, and the
/// code — which is optional, because an account without an authenticator has none — must be six digits
/// when it is present.
/// </summary>
/// <remarks>
/// Whether the password or the code is <em>right</em> is the flow's answer (401), never this validator's:
/// a malformed body is a 400 <c>validation_failed</c> and costs no attempt.
/// </remarks>
public sealed class ReauthValidator : AbstractValidator<ReauthCommand>
{
    /// <summary>The longest accepted password input; Identity hashes whatever it is given.</summary>
    public const int PasswordMaxLength = 128;

    /// <summary>The digits of an authenticator code (RFC 6238, BD4: 6).</summary>
    public const int CodeLength = 6;

    public ReauthValidator()
    {
        RuleFor(command => command.Password)
            .NotEmpty().WithMessage("password_required")
            .MaximumLength(PasswordMaxLength).WithMessage("password_too_long");

        RuleFor(command => command.Code)
            .Length(CodeLength).WithMessage("code_invalid")
            .Matches("^[0-9]{6}$").WithMessage("code_invalid")
            .When(command => !string.IsNullOrEmpty(command.Code));
    }
}
