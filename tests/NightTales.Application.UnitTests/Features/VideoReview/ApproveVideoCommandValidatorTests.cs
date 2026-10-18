using NightTales.Application.Features.VideoReview.Commands.ApproveVideo;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.VideoReview;

/// <summary>
/// The approval validator of 09.7.1-T1: an episode id and a note bounded by the column contract — and nothing
/// else, because the state and the ETag are the handler's guards, not the validator's.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ApproveVideoCommandValidatorTests
{
    private readonly ApproveVideoCommandValidator _validator = new();

    [Fact]
    public async Task ValidCommand_Passes()
    {
        (await ValidateAsync(new ApproveVideoCommand(Guid.CreateVersion7(), "Good cut, ship it."))).IsValid
            .ShouldBeTrue();
    }

    [Fact]
    public async Task NullNote_Passes()
    {
        (await ValidateAsync(new ApproveVideoCommand(Guid.CreateVersion7(), null))).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task NoteAtTheBound_Passes_AndOneCharacterMoreFails()
    {
        var id = Guid.CreateVersion7();

        (await ValidateAsync(new ApproveVideoCommand(id, new string('n', ApproveVideoCommandValidator.MaxNoteLength))))
            .IsValid.ShouldBeTrue("the bound is inclusive");

        var tooLong = await ValidateAsync(
            new ApproveVideoCommand(id, new string('n', ApproveVideoCommandValidator.MaxNoteLength + 1)));
        tooLong.IsValid.ShouldBeFalse();
        tooLong.Errors.ShouldContain(error => error.PropertyName == nameof(ApproveVideoCommand.Note));
    }

    [Fact]
    public async Task EmptyEpisodeId_Fails()
    {
        var result = await ValidateAsync(new ApproveVideoCommand(Guid.Empty, null));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(ApproveVideoCommand.EpisodeId));
    }

    private Task<FluentValidation.Results.ValidationResult> ValidateAsync(ApproveVideoCommand command) =>
        _validator.ValidateAsync(command, TestContext.Current.CancellationToken);
}
