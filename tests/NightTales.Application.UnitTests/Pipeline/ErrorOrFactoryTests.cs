using ErrorOr;
using NightTales.Application.Common.Errors;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Pipeline;

public sealed class ErrorOrFactoryTests
{
    [Fact]
    public void ErrorOrFactory_ConvertsErrorsForAnyErrorOrType()
    {
        var errors = new List<Error>
        {
            Error.Validation(
                ErrorCodes.ValidationFailed,
                "name_required",
                new Dictionary<string, object> { ["field"] = "name" }),
            Error.Conflict("episode_invalid_state", "invalid state"),
        };

        var withValue = ErrorOrFactory<ErrorOr<Guid>>.From(errors);
        withValue.IsError.ShouldBeTrue();
        withValue.Errors.ShouldBe(errors);

        var withSuccess = ErrorOrFactory<ErrorOr<Success>>.From(errors);
        withSuccess.IsError.ShouldBeTrue();
        withSuccess.Errors.ShouldBe(errors);
    }
}
