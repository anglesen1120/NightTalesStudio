using NightTales.Application.Features.Account.Legal.Commands.AcceptLegalDocuments;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Account.Legal;

/// <summary>QA probes: hostile version strings never validate.</summary>
public sealed class AcceptLegalDocumentsValidatorQaTests
{
    private readonly AcceptLegalDocumentsValidator _validator = new();

    [Theory]
    [InlineData(" 1.0.0")]
    [InlineData("1.0.0 ")]
    [InlineData("1.0.0\r\n")]
    [InlineData("1.0.0\0")]
    [InlineData("\u00a01.0.0")]
    [InlineData("1.0.0\ud800")]
    [InlineData("1.0.0.0")]
    [InlineData("+1.0.0")]
    [InlineData("-1.0.0")]
    [InlineData("1..0")]
    [InlineData("\u0661.\u0660.\u0660")]
    [InlineData("\uff11.\uff10.\uff10")]
    public void HostileVersion_IsInvalid_OnEitherField(string version)
    {
        _validator.Validate(new AcceptLegalDocumentsCommand(version, "1.0.0")).IsValid.ShouldBeFalse("tos");
        _validator.Validate(new AcceptLegalDocumentsCommand("1.0.0", version)).IsValid.ShouldBeFalse("privacy");
    }

    [Fact]
    public void ErrorsAreGroupedPerOffendingField()
    {
        var result = _validator.Validate(new AcceptLegalDocumentsCommand("v1", "1.0.0"));
        result.Errors.Select(failure => failure.PropertyName).Distinct().ShouldBe([nameof(AcceptLegalDocumentsCommand.Tos)]);
    }
}
