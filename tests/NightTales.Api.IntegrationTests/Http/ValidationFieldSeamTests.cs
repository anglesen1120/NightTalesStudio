using ErrorOr;
using FluentValidation;
using Mediator;
using NightTales.Api.Http;
using NightTales.Application.Behaviors;
using NightTales.Application.Common.Errors;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// The <c>field</c> seam of 09.1.4-T1: <see cref="ValidationBehavior{TMessage,TResponse}"/> writes the
/// metadata key and <see cref="ProblemDetailsWriter"/> reads the value behind it to group
/// <c>errors</c>. Both sides go through <see cref="ErrorMetadata.Field"/>, and this test drives the
/// real behavior — no host, no HTTP — into the real writer, so a literal on either side breaks it.
/// </summary>
public sealed class ValidationFieldSeamTests
{
    [Fact]
    public async Task ValidationBehavior_WritesTheSharedFieldKey_AndTheWriterGroupsByIt()
    {
        var behavior = new ValidationBehavior<SeamCommand, ErrorOr<SeamCommand>>([new SeamCommandValidator()]);
        var handlerRan = false;

        var result = await behavior.Handle(
            new SeamCommand(string.Empty),
            (_, _) =>
            {
                handlerRan = true;
                return ValueTask.FromResult<ErrorOr<SeamCommand>>(new SeamCommand("settled"));
            },
            CancellationToken.None);

        handlerRan.ShouldBeFalse("a failing validator short-circuits the pipeline");
        result.IsError.ShouldBeTrue();
        result.Errors.ShouldHaveSingleItem();
        var error = result.Errors[0];
        error.Metadata.ShouldNotBeNull();
        error.Metadata.Keys.ShouldBe([ErrorMetadata.Field]);
        error.Metadata[ErrorMetadata.Field].ShouldBe("title");

        using var exchange = new ProblemExchange();
        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(result.Errors, exchange.Context));

        response.ShouldNotBeNull();
        response.Status.ShouldBe(400);
        response.Code.ShouldBe(ErrorCodes.ValidationFailed);
        response.Errors.ShouldNotBeNull();
        // The writer groups by the value under the same key the behavior wrote.
        response.Errors.Keys.ShouldBe(["title"]);
        response.Errors["title"].ShouldBe(["title_required"]);
    }

    private sealed record SeamCommand(string? Title) : IMessage;

    private sealed class SeamCommandValidator : AbstractValidator<SeamCommand>
    {
        public SeamCommandValidator() => RuleFor(command => command.Title).NotEmpty().WithMessage("title_required");
    }
}
