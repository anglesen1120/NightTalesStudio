using FluentValidation;

namespace NightTales.Application.Features.Auth.Login.Mfa.Commands;

/// <summary>
/// Shape validation of <c>POST /auth/login/mfa</c> (09.3.2-T3 Design): the <c>mfaToken</c> is required
/// and exactly one of the two codes must be present.
/// </summary>
/// <remarks>
/// <para>
/// Whether a code is <em>right</em> is the flow's answer (401 <c>invalid_mfa_code</c>), never this
/// validator's: a malformed body is a 400 <c>validation_failed</c> and costs no attempt.
/// </para>
/// <para>
/// The mutual-exclusion rule is the Design's: a caller cannot brute-force the authenticator and the
/// recovery-code surface in one request, and the recovery-code list is only ever touched when the
/// request really was a recovery-code request. A TOTP code presented as <c>recoveryCode</c> is not
/// rejected here — it is simply not a recovery code, and the redemption fails.
/// </para>
/// </remarks>
public sealed class LoginMfaValidator : AbstractValidator<LoginMfaCommand>
{
    /// <summary>The digits of an authenticator code (RFC 6238, BD4: 6).</summary>
    public const int CodeLength = 6;

    /// <summary>The characters of a Crockford base32 recovery code (BD4: 10).</summary>
    public const int RecoveryCodeLength = 10;

    /// <summary>
    /// The longest accepted <c>mfaToken</c>: the issued value is 43 base64url characters (32 random
    /// bytes), and a longer submission cannot name a stored challenge.
    /// </summary>
    public const int MfaTokenMaxLength = 128;

    public LoginMfaValidator()
    {
        RuleFor(command => command.MfaToken)
            .NotEmpty().WithMessage("mfa_token_required")
            .MaximumLength(MfaTokenMaxLength).WithMessage("mfa_token_invalid");

        RuleFor(command => command.Code)
            .Length(CodeLength).WithMessage("code_invalid")
            .Matches("^[0-9]{6}$").WithMessage("code_invalid")
            .When(command => !string.IsNullOrEmpty(command.Code));

        RuleFor(command => command.RecoveryCode)
            .Must(Normalize).WithMessage("recovery_code_invalid")
            .When(command => !string.IsNullOrEmpty(command.RecoveryCode));

        // The Design's mutual exclusion: exactly one of the two code fields. The failure is reported
        // against `code`, because a request that breaks it is malformed whichever field it filled in.
        RuleFor(command => command)
            .Must(command =>
                string.IsNullOrEmpty(command.Code) != string.IsNullOrEmpty(command.RecoveryCode))
            .WithName("code")
            .WithMessage("code_or_recovery_code_required");
    }

    /// <summary>
    /// Whether the submitted text is a well-formed recovery code, ignoring the dashes and spaces a user
    /// may have copied from the printed list; the Crockford alphabet is checked case-insensitively,
    /// because the letters <c>I</c>, <c>L</c>, <c>O</c> and <c>U</c> are not part of it.
    /// </summary>
    private static bool Normalize(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        var normalized = code.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        if (normalized.Length != RecoveryCodeLength)
        {
            return false;
        }

        foreach (var character in normalized)
        {
            if (!IsCrockford(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The Crockford base32 alphabet, without the four letters it deliberately omits.</summary>
    private static bool IsCrockford(char character) => character switch
    {
        >= '0' and <= '9' => true,
        'I' or 'L' or 'O' or 'U' => false,
        >= 'A' and <= 'Z' => true,
        _ => false,
    };
}
