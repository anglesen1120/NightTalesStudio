using FluentValidation.Results;
using NightTales.Application.Features.SeriesBible.Commands.UpdateBible;
using NightTales.Domain.Series.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible.Commands.UpdateBible;

/// <summary>
/// Shape validation of <c>PUT /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U2): the series id and the
/// presence of the content, and deliberately nothing else — the <c>If-Match</c> header belongs to the
/// handler (428/412, never a 400) and the content rules to the Domain validator, which keeps the field paths
/// the 400 reports.
/// </summary>
public sealed class UpdateBibleCommandValidatorTests
{
    private static readonly UpdateBibleCommandValidator Validator = new();

    private static ValidationResult Check(Guid seriesId, SeriesBibleContent? content, string? ifMatch = "\"sb-1\"") =>
        Validator.Validate(new UpdateBibleCommand(seriesId, content!, ifMatch));

    [Fact]
    public void Valid_Command_Passes() =>
        Check(Guid.CreateVersion7(), SeriesBibleContent.Empty).IsValid.ShouldBeTrue();

    [Fact]
    public void EmptySeriesId_Fails() =>
        Check(Guid.Empty, SeriesBibleContent.Empty).Errors.ShouldContain(
            error => error.PropertyName == nameof(UpdateBibleCommand.SeriesId));

    [Fact]
    public void MissingContent_Fails() =>
        Check(Guid.CreateVersion7(), null).Errors.ShouldContain(
            error => error.PropertyName == nameof(UpdateBibleCommand.Content));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an etag")]
    public void IfMatch_IsNeverAValidationFailure(string? ifMatch) =>
        Check(Guid.CreateVersion7(), SeriesBibleContent.Empty, ifMatch).IsValid.ShouldBeTrue();
}
