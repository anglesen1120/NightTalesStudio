using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Common.Errors;
using NightTales.Application.UnitTests.Pipeline.Probes;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Pipeline;

public sealed class ValidationBehaviorTests
{
    [Fact]
    public async Task Validation_Failure_DoesNotInvokeHandler()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ProbeCommand(string.Empty), CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        result.FirstError.Description.ShouldBe("name_required");
        result.FirstError.Metadata.ShouldNotBeNull();
        result.FirstError.Metadata!["field"].ShouldBe("name");
        host.Trace.Entries.ShouldNotContain("handler");
    }

    [Fact]
    public async Task Validation_MultipleFailures_AreAllReturned()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ProbeCommand(string.Empty, ProbeCommand.ForbiddenSecret), CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Errors.Count.ShouldBe(2);
        result.Errors.Select(error => error.Code).ShouldBe([ErrorCodes.ValidationFailed, ErrorCodes.ValidationFailed]);
        result.Errors.Select(error => error.Description).ShouldBe(["name_required", "secret_forbidden"]);
        result.Errors.Select(error => (string)error.Metadata!["field"]).ShouldBe(["name", "secret"]);
        host.Trace.Entries.ShouldNotContain("handler");
    }
}
