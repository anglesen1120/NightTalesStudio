using NightTales.Application.Features.VideoReview.Commands.RejectVideo;
using NightTales.Domain.Episodes;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.VideoReview;

/// <summary>
/// The rejection validator of 09.7.1-T1: a non-blank reason of at most <c>varchar(500)</c> characters <em>after
/// trimming</em> (the handler stores the trimmed text), an episode id and a target the enum declares.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RejectVideoCommandValidatorTests
{
    private readonly RejectVideoCommandValidator _validator = new();

    [Fact]
    public async Task ValidCommand_Passes()
    {
        (await ValidateAsync(Command(reason: "Too dark at the start"))).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task BlankReason_Fails(string reason)
    {
        var result = await ValidateAsync(Command(reason));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RejectVideoCommand.Reason));
    }

    [Fact]
    public async Task MissingReason_Fails()
    {
        // The route's reader can only produce a null reason for an absent member; the record's contract says a
        // reason is required, so the validator is the guard that answers 400 instead of the handler.
        (await ValidateAsync(Command(null!))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task ReasonIsMeasuredAfterTrimming()
    {
        // 500 characters plus the editor's padding: the stored text is the trimmed one, so it fits the column.
        var padded = "  " + new string('r', RejectVideoCommandValidator.MaxReasonLength) + "  ";
        (await ValidateAsync(Command(padded))).IsValid.ShouldBeTrue("the trimmed length is what the column stores");

        var tooLong = await ValidateAsync(Command(new string('r', RejectVideoCommandValidator.MaxReasonLength + 1)));
        tooLong.IsValid.ShouldBeFalse();
        tooLong.Errors.ShouldContain(error => error.PropertyName == nameof(RejectVideoCommand.Reason));
    }

    [Fact]
    public async Task EmptyEpisodeId_Fails()
    {
        var result = await ValidateAsync(
            new RejectVideoCommand(Guid.Empty, "Too dark at the start", RejectTarget.Story));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RejectVideoCommand.EpisodeId));
    }

    [Fact]
    public async Task TargetOutsideTheEnum_Fails()
    {
        var result = await ValidateAsync(Command("Too dark at the start", (RejectTarget)99));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RejectVideoCommand.Target));
    }

    private static RejectVideoCommand Command(string reason, RejectTarget target = RejectTarget.Story) =>
        new(Guid.CreateVersion7(), reason, target);

    private Task<FluentValidation.Results.ValidationResult> ValidateAsync(RejectVideoCommand command) =>
        _validator.ValidateAsync(command, TestContext.Current.CancellationToken);
}
