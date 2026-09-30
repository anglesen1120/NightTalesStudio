using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Features.Auth.Register.Commands;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Auth;

/// <summary>
/// The shape validation of <c>POST /api/v1/auth/register</c> (09.3.1-T1 Outputs, run by the D4
/// <c>ValidationBehavior</c> before the handler): the e-mail bound, the password bound, and exactly
/// one acceptance per document with a plausible version. The wire contract is the frozen body of
/// 10-web-ui SC30, so every rejection here is a client-visible boundary rather than an internal detail.
/// </summary>
public sealed class RegisterValidatorTests
{
    private readonly RegisterValidator _validator = new();

    [Fact]
    public void ValidCommand_Passes() => _validator.Validate(Command()).IsValid.ShouldBeTrue();

    [Fact]
    public void MissingTosAcceptance_IsRejected()
    {
        var result = _validator.Validate(Command(consents: [Privacy("v1")]));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.ErrorMessage == "tos_acceptance_required");
    }

    [Fact]
    public void MissingPrivacyAcceptance_IsRejected()
    {
        var result = _validator.Validate(Command(consents: [Tos("v1")]));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.ErrorMessage == "privacy_acceptance_required");
    }

    [Fact]
    public void DuplicateAcceptanceForOneDocument_IsRejected()
    {
        // Two tos rows are as wrong as none: the body carries exactly one acceptance per document.
        var result = _validator.Validate(Command(consents: [Tos("v1"), Tos("v2"), Privacy("v1")]));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.ErrorMessage == "tos_acceptance_required");
    }

    [Fact]
    public void Consents_WithUnknownDocument_IsRejected()
    {
        // An integer (or future member) outside Tos/Privacy is not a known acceptance: the shape rule
        // must refuse it here, or it reaches the user_consents CHECK constraint as a raw
        // DbUpdateException instead of validation_failed (09.3.1-T1 failure handling).
        var result = _validator.Validate(
            Command(consents: [Tos("v1"), Privacy("v1"), new ConsentAcceptance((LegalDocument)7, "v1")]));

        result.IsValid.ShouldBeFalse(
            "a consent entry whose document is not tos or privacy is not a valid acceptance");
    }

    [Fact]
    public void BlankConsentVersion_IsRejected()
    {
        var result = _validator.Validate(Command(consents: [Tos("  "), Privacy("v1")]));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.ErrorMessage == "consent_version_invalid");
    }

    [Fact]
    public void ConsentVersion_AtThe32CharacterBound_IsAccepted_But33IsRejected()
    {
        _validator.Validate(Command(consents: [Tos(new string('v', 32)), Privacy("v1")]))
            .IsValid.ShouldBeTrue("32 is the width of user_consents.version");

        var overLong = _validator.Validate(Command(consents: [Tos(new string('v', 33)), Privacy("v1")]));

        overLong.IsValid.ShouldBeFalse();
        overLong.Errors.ShouldContain(failure => failure.ErrorMessage == "consent_version_invalid");
    }

    [Fact]
    public void InvalidEmail_IsRejected()
    {
        var result = _validator.Validate(Command(email: "not-an-address"));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.ErrorMessage == "email_invalid");
    }

    [Fact]
    public void EmailOver254Characters_IsRejected()
    {
        var result = _validator.Validate(Command(email: new string('a', 60) + "@" + new string('b', 200) + ".test"));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.ErrorMessage == "email_too_long");
    }

    [Fact]
    public void EmptyPassword_IsRejected()
    {
        var result = _validator.Validate(Command(password: string.Empty));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.ErrorMessage == "password_required");
    }

    [Fact]
    public void Password_AtThe128CharacterBound_IsAccepted_But129IsRejected()
    {
        _validator.Validate(Command(password: new string('A', 128))).IsValid.ShouldBeTrue();

        var overLong = _validator.Validate(Command(password: new string('A', 129)));

        overLong.IsValid.ShouldBeFalse();
        overLong.Errors.ShouldContain(failure => failure.ErrorMessage == "password_too_long");
    }

    private static RegisterCommand Command(
        string email = "user@nighttales.test",
        string password = "Abcdefghijkl",
        IReadOnlyList<ConsentAcceptance>? consents = null)
        => new(email, password, consents ?? [Tos("v1"), Privacy("v1")]);

    private static ConsentAcceptance Tos(string version) => new(LegalDocument.Tos, version);

    private static ConsentAcceptance Privacy(string version) => new(LegalDocument.Privacy, version);
}
