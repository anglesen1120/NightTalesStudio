using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Common.Errors;
using NightTales.Application.UnitTests.Pipeline.Probes;
using NightTales.Domain.Common;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Pipeline;

/// <summary>
/// <c>AuthorizationBehavior</c> (09 README D4, 09.1.3-T2): deny by default, honour the explicit
/// <c>[AllowAnonymousRequest]</c> opt-out, answer a role below the requirement with 404 rather than 403,
/// and let the <c>system</c> actor pass user requirements only.
/// </summary>
public sealed class AuthorizationBehaviorTests
{
    [Fact]
    public async Task Authorization_Anonymous_Returns401_UnlessAllowAnonymous()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        host.CurrentUser.Id = null;

        var denied = await sender.Send(new ProbeCommand("probe"), CancellationToken.None);

        denied.IsError.ShouldBeTrue();
        denied.FirstError.Code.ShouldBe(ErrorCodes.Unauthorized);
        host.Trace.Entries.ShouldNotContain("handler", "the check runs before the handler");
        host.Trace.Entries.ShouldNotContain("validate", "the check runs before validation");

        var allowed = await sender.Send(new AnonymousProbeCommand("probe"), CancellationToken.None);

        allowed.IsError.ShouldBeFalse();
        allowed.Value.ShouldBe(AnonymousProbeCommandHandler.ResultId);
        host.Trace.Entries.ShouldContain("anonymous-handler");
    }

    [Fact]
    public async Task Authorization_InsufficientRole_Returns404()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // A plain user is below the owner requirement: the answer is the 404 a missing row gets, so the
        // owner surface never confirms that it exists (09 README D10/§5.4 — never 403).
        var denied = await sender.Send(new OwnerProbeCommand("probe"), CancellationToken.None);

        denied.IsError.ShouldBeTrue();
        denied.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        host.Trace.Entries.ShouldNotContain("owner-handler");

        host.CurrentUser.Role = PlatformRole.Owner;

        var allowed = await sender.Send(new OwnerProbeCommand("probe"), CancellationToken.None);

        allowed.IsError.ShouldBeFalse();
        allowed.Value.ShouldBe(OwnerProbeCommandHandler.ResultId);
        host.Trace.Entries.ShouldContain("owner-handler");
    }

    [Fact]
    public async Task Authorization_SystemActor_PassesUserRequirementsOnly()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // The highest role the platform has, held by a job: a system actor never performs an admin action
        // [DECISION 09.1.3-T2], so only a PlatformRole.User requirement passes.
        host.CurrentUser.Actor = ActorType.System;
        host.CurrentUser.Role = PlatformRole.Owner;

        var denied = await sender.Send(new OwnerProbeCommand("probe"), CancellationToken.None);
        denied.IsError.ShouldBeTrue();
        denied.FirstError.Code.ShouldBe(ErrorCodes.NotFound);

        var allowed = await sender.Send(new ProbeCommand("probe"), CancellationToken.None);
        allowed.IsError.ShouldBeFalse();
        allowed.Value.ShouldBe(ProbeCommandHandler.ResultId);
    }
}
