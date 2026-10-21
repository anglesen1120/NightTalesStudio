using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.UnitTests.Pipeline.Probes;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Pipeline;

public sealed class LoggingBehaviorTests
{
    [Fact]
    public async Task Logging_NeverWritesMessageValues()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ProbeCommand("probe"), CancellationToken.None);

        result.IsError.ShouldBeFalse();

        var messages = host.Logs.Messages;
        messages.ShouldHaveSingleItem();
        messages.ShouldContain(line => line.Contains("mediator.request", StringComparison.Ordinal));
        messages.ShouldContain(line => line.Contains("message=ProbeCommand", StringComparison.Ordinal));
        messages.ShouldContain(line => line.Contains("outcome=ok", StringComparison.Ordinal));
        messages.ShouldAllBe(line => !line.Contains(ProbeCommand.SecretValue, StringComparison.Ordinal));
        messages.ShouldAllBe(line => !line.Contains("probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logging_OnError_LogsTheFirstErrorCodeAtWarningLevel()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ProbeCommand(string.Empty), CancellationToken.None);

        result.IsError.ShouldBeTrue();

        var messages = host.Logs.Messages;
        messages.ShouldHaveSingleItem();
        messages.ShouldContain(line => line.StartsWith("Warning:", StringComparison.Ordinal));
        messages.ShouldContain(line => line.Contains("outcome=validation_failed", StringComparison.Ordinal));
        messages.ShouldAllBe(line => !line.Contains(ProbeCommand.SecretValue, StringComparison.Ordinal));
    }
}
