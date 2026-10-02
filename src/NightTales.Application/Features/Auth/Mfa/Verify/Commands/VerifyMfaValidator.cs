using FluentValidation;

namespace NightTales.Application.Features.Auth.Mfa.Verify.Commands;

/// <summary>
/// Shape validation of <c>POST /me/mfa/verify</c> (09.10.4-T1 Design), run by the D4
/// <c>ValidationBehavior</c> before the handler: a malformed code is 400 <c>validation_failed</c>, while a
/// well-formed code that simply does not match is the 401 <c>invalid_mfa_code</c> the flow answers.
/// </summary>
/// <remarks>
/// The shape is RFC 6238's: exactly six digits. Nothing here decides whether the code is correct — that
/// is the verifier's answer, and keeping the two apart is what stops this route from becoming an oracle.
/// </remarks>
public sealed class VerifyMfaValidator : AbstractValidator<VerifyMfaCommand>
{
    /// <summary>The digits of a code (RFC 6238, BD4: 6).</summary>
    public const int CodeLength = 6;

    public VerifyMfaValidator()
    {
        RuleFor(command => command.Code)
            .NotEmpty().WithMessage("code_required")
            .Length(CodeLength).WithMessage("code_invalid")
            .Matches("^[0-9]{6}$").WithMessage("code_invalid");
    }
}
