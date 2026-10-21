using NightTales.Application.Features.Account.Legal.Commands.AcceptLegalDocuments;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Account.Legal;

public sealed class AcceptLegalDocumentsValidatorTests
{
    private readonly AcceptLegalDocumentsValidator _validator = new();

    [Theory]
    [InlineData("v1")]
    [InlineData("1.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0\n")]
    public void VersionNotSemver_ReturnsValidationFailed(string version)
    {
        _validator.Validate(new AcceptLegalDocumentsCommand(version, "1.0.0")).IsValid.ShouldBeFalse();
        _validator.Validate(new AcceptLegalDocumentsCommand("1.0.0", version)).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingDocument_ReturnsValidationFailed(string? version)
    {
        _validator.Validate(new AcceptLegalDocumentsCommand(version!, "1.0.0")).IsValid.ShouldBeFalse();
        _validator.Validate(new AcceptLegalDocumentsCommand("1.0.0", version!)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void VersionOver32Chars_ReturnsValidationFailed()
    {
        var oversized = new string('1', 29) + ".0.0";
        _validator.Validate(new AcceptLegalDocumentsCommand(oversized, "1.0.0")).IsValid.ShouldBeFalse();
        _validator.Validate(new AcceptLegalDocumentsCommand("1.0.0", oversized)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void VersionAt32Chars_IsValid()
    {
        var boundary = new string('1', 28) + ".0.0";
        _validator.Validate(new AcceptLegalDocumentsCommand(boundary, boundary)).IsValid.ShouldBeTrue();
    }
}
