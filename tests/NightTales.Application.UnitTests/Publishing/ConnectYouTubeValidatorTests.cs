using FluentValidation.Results;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Channels.Commands.ConnectYouTube;
using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// The connect route's shape check and its platform mapping (06 README D3, 06.1.2-T1 §Tests): the
/// validator refuses a segment that cannot be a platform, an unserved but plausible platform stays the
/// handler's 404 <c>not_found</c>, and <c>youtube</c> passes both.
/// </summary>
/// <remarks>
/// No mocking library (09 D13): the handler is built with the collaborators this path never reads, which is
/// exactly what the test asserts — step 1 answers before the caller, the clock, the resolver or the
/// database is touched.
/// </remarks>
public sealed class ConnectYouTubeValidatorTests
{
    private static readonly ConnectYouTubeValidator Validator = new();

    [Fact]
    public void Validate_YouTubePlatform_Passes()
    {
        var result = Validator.Validate(new ConnectYouTubeCommand("youtube"));

        result.IsValid.ShouldBeTrue(string.Join("; ", result.Errors.Select(error => error.ErrorMessage)));
    }

    [Fact]
    public void Validate_CaseInsensitiveWireValue_Passes()
    {
        // PlatformExtensions.TryParse ignores case, so the validator must not be stricter than the handler.
        var result = Validator.Validate(new ConnectYouTubeCommand("YouTube"));

        result.IsValid.ShouldBeTrue(string.Join("; ", result.Errors.Select(error => error.ErrorMessage)));
    }

    [Fact]
    public void Validate_UnservedPlatform_PassesAndLeavesThe404ToTheHandler()
    {
        // 'tiktok' arrives with 07. Until then it is a resource the API does not serve: the frozen answer is
        // the handler's 404 not_found. A 400 here would tell a probe which platforms exist.
        var result = Validator.Validate(new ConnectYouTubeCommand("tiktok"));

        result.IsValid.ShouldBeTrue(string.Join("; ", result.Errors.Select(error => error.ErrorMessage)));
    }

    [Fact]
    public void Validate_SegmentThatCannotBeAPlatform_FailsOnTheField()
    {
        var result = Validator.Validate(
            new ConnectYouTubeCommand(new string('a', ConnectYouTubeValidator.MaxPlatformLength + 1)));

        result.IsValid.ShouldBeFalse();
        ValidationFailure failure = result.Errors.ShouldHaveSingleItem();
        failure.PropertyName.ShouldBe(nameof(ConnectYouTubeCommand.Platform));
    }

    [Fact]
    public async Task Handle_UnservedPlatform_AnswersNotFound()
    {
        var handler = new ConnectYouTubeHandler(
            reauth: null!,
            legal: null!,
            currentUser: null!,
            resolver: null!,
            database: null!,
            protector: null!,
            clock: null!,
            options: null!,
            youTube: null!,
            logger: NullLogger<ConnectYouTubeHandler>.Instance);

        var answer = await handler.Handle(
            new ConnectYouTubeCommand("tiktok"),
            TestContext.Current.CancellationToken);

        answer.IsError.ShouldBeTrue();
        answer.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
    }

    [Fact]
    public void PlatformWireMapping_IsLowercaseYouTube()
    {
        // The one wire value this task serves; the handler's 404 is the other half of the same mapping.
        Platform.YouTube.ToWire().ShouldBe("youtube");
        PlatformExtensions.TryParse("tiktok", out _).ShouldBeFalse();
        PlatformExtensions.TryParse("youtube", out var parsed).ShouldBeTrue();
        parsed.ShouldBe(Platform.YouTube);
    }
}
