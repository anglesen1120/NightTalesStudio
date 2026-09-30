using FluentValidation;
using NightTales.Application.Abstractions.Identity;

namespace NightTales.Application.Features.Auth.Register.Commands;

/// <summary>
/// Shape validation of <c>POST /auth/register</c> (09.3.1-T3 §Design), run by the D4
/// <c>ValidationBehavior</c> <em>before</em> the handler and the account lookup, so a malformed body
/// can never become an enumeration oracle.
/// </summary>
/// <remarks>
/// <para>
/// It checks only what the wire fixes: the e-mail's length and syntax, the password's length, and
/// exactly one acceptance per document with a plausible version. Whether the version is
/// <em>current</em> is 13's rule, not this validator's (a stale version is accepted and recorded;
/// re-acceptance is 09.3.1-T3/13).
/// </para>
/// <para>
/// 09.3.1-T3 extends this validator (invite code, locale) instead of adding a second one for the same
/// request folder, which the architecture rules allow at most one of (09.1.2-T1).
/// </para>
/// </remarks>
public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    /// <summary>The longest accepted version string, matching <c>user_consents.version</c> (32).</summary>
    public const int VersionMaxLength = 32;

    /// <summary>The longest accepted e-mail address (RFC 5321 path limit).</summary>
    public const int EmailMaxLength = 254;

    /// <summary>The longest accepted password input; Identity hashes whatever it is given.</summary>
    public const int PasswordMaxLength = 128;

    public RegisterValidator()
    {
        RuleFor(command => command.Email)
            .NotEmpty().WithMessage("email_required")
            .MaximumLength(EmailMaxLength).WithMessage("email_too_long")
            .EmailAddress().WithMessage("email_invalid");

        RuleFor(command => command.Password)
            .NotEmpty().WithMessage("password_required")
            .MaximumLength(PasswordMaxLength).WithMessage("password_too_long");

        RuleFor(command => command.Consents)
            .Must(consents => consents is not null
                && consents.Count(consent => consent is not null && consent.Document is LegalDocument.Tos) == 1)
            .WithMessage("tos_acceptance_required")
            .Must(consents => consents is not null
                && consents.Count(consent => consent is not null && consent.Document is LegalDocument.Privacy) == 1)
            .WithMessage("privacy_acceptance_required")
            // The converter accepts integer enum values, so an out-of-range number would otherwise reach
            // the flow as an undefined document (09.3.1-T1 QA D3).
            .Must(consents => consents is not null
                && consents.All(consent => consent is not null && Enum.IsDefined(consent.Document)))
            .WithMessage("consent_document_unknown")
            .Custom((consents, context) =>
            {
                if (consents is null)
                {
                    return;
                }

                for (var index = 0; index < consents.Count; index++)
                {
                    var consent = consents[index];
                    if (consent is null)
                    {
                        // The null entry has its own failure below; nothing else can be checked on it.
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(consent.Version)
                        || consent.Version.Length > VersionMaxLength)
                    {
                        context.AddFailure($"Consents[{index}].Version", "consent_version_invalid");
                    }
                }
            });

        // `consents: [null]` is a legal JSON payload, so each entry is checked before any predicate reads
        // its members: a null entry must be a 400 shape error, never a NullReferenceException (QA C1).
        RuleForEach(command => command.Consents)
            .NotNull().WithMessage("consent_entry_required");
    }
}
