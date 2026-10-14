using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Series.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// The typed errors of the series bible (02 README D8/D9): the codes the Domain spells itself must equal the
/// catalogue's, and the <c>bible_invalid_content</c> error must hand its field list to the ProblemDetails writer.
/// </summary>
public sealed class BibleErrorsTests
{
    [Fact]
    public void BibleErrors_Codes_MatchTheErrorCatalogue()
    {
        // The Domain cannot reference Application/Common/Errors/ErrorCodes.cs (D1: ErrorOr is its only package),
        // so it spells the five codes itself; the HTTP status each maps to is the Api catalogue's. This test is the
        // join between the two, and it is why a rename cannot slip through unnoticed.
        BibleErrors.InvalidContentCode.ShouldBe(ErrorCodes.BibleInvalidContent);
        BibleErrors.ConflictCode.ShouldBe(ErrorCodes.BibleConflict);
        BibleErrors.ProposalNotPendingCode.ShouldBe(ErrorCodes.BibleProposalNotPending);
        BibleErrors.IfMatchRequiredCode.ShouldBe(ErrorCodes.IfMatchRequired);
        BibleErrors.MissingCode.ShouldBe(ErrorCodes.SeriesBibleMissing);
    }

    [Fact]
    public void BibleErrors_InvalidContent_CarriesTheFieldList()
    {
        var fieldErrors = new List<BibleFieldError>
        {
            new("recurring_characters", "too_many", "30"),
            new("rules[1].text", "duplicate", "rules[0]"),
        };

        var error = BibleErrors.InvalidContent(fieldErrors);

        error.Code.ShouldBe(BibleErrors.InvalidContentCode);
        error.Type.ShouldBe(ErrorType.Validation);
        BibleErrors.TryGetFieldErrors(error, out var restored).ShouldBeTrue();
        restored.ShouldBe(fieldErrors);
    }

    [Fact]
    public void BibleErrors_InvalidContent_WithoutFields_Throws()
    {
        Should.Throw<ArgumentException>(() => BibleErrors.InvalidContent([])).ParamName.ShouldBe("errors");
    }

    [Fact]
    public void BibleErrors_OtherErrors_CarryNoFieldList()
    {
        var conflict = BibleErrors.Conflict();
        var notPending = BibleErrors.ProposalNotPending();
        var ifMatchRequired = BibleErrors.IfMatchRequired();
        var missing = BibleErrors.Missing();

        conflict.Code.ShouldBe(BibleErrors.ConflictCode);
        conflict.Type.ShouldBe(ErrorType.Conflict);
        notPending.Code.ShouldBe(BibleErrors.ProposalNotPendingCode);
        notPending.Type.ShouldBe(ErrorType.Conflict);
        ifMatchRequired.Type.ShouldBe(ErrorType.Failure);
        missing.Code.ShouldBe(BibleErrors.MissingCode);
        missing.Type.ShouldBe(ErrorType.Failure);

        BibleErrors.TryGetFieldErrors(conflict, out var errors).ShouldBeFalse();
        errors.ShouldBeEmpty();
        BibleErrors.TryGetFieldErrors(missing, out var missingErrors).ShouldBeFalse();
        missingErrors.ShouldBeEmpty();
    }
}
