using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Identity;

/// <summary>
/// <c>IdentityPasswordPolicy</c> (09.3.1-T1 Tests table): the BD2 length rule and the breached-password
/// answer, with a hand-written checker — no mocking library (09 README D13).
/// </summary>
public sealed class PasswordPolicyTests
{
    private const string TwelveCharsNoComposition = "Abcdefghijkl";

    [Fact]
    public async Task ElevenChars_ReturnsValidationFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var policy = CreatePolicy(new StubBreachedPasswordChecker(breached: false));

        var result = await policy.ValidateAsync("Abcdefghijk", ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.FirstError.Description.ShouldBe("PasswordTooShort", "the description is Identity's own error code");
    }

    [Fact]
    public async Task TwelveCharsNoCompositionRules_IsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var policy = CreatePolicy(new StubBreachedPasswordChecker(breached: false));

        var result = await policy.ValidateAsync(TwelveCharsNoComposition, ct);

        result.IsError.ShouldBeFalse("BD2 turns every composition rule off, so length is the only rule");
    }

    [Fact]
    public async Task BreachedPassword_ReturnsBreachedPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        var policy = CreatePolicy(new StubBreachedPasswordChecker(breached: true));

        var result = await policy.ValidateAsync(TwelveCharsNoComposition, ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.BreachedPassword);
    }

    [Fact]
    public async Task UnreachableSource_PropagatesTheFailure_NeverAcceptsThePassword()
    {
        var ct = TestContext.Current.CancellationToken;
        var policy = CreatePolicy(new UnreachableBreachedPasswordChecker());

        var result = await policy.ValidateAsync(TwelveCharsNoComposition, ct);

        result.IsError.ShouldBeTrue("range mode fails closed (503 service_unavailable)");
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    private static IdentityPasswordPolicy CreatePolicy(IBreachedPasswordChecker checker) =>
        new(
            Options.Create(new IdentityOptions { Password = { RequiredLength = 12 } }),
            checker);

    private sealed class StubBreachedPasswordChecker(bool breached) : IBreachedPasswordChecker
    {
        public Task<ErrorOr<bool>> IsBreachedAsync(string password, CancellationToken ct) =>
            Task.FromResult<ErrorOr<bool>>(breached);
    }

    private sealed class UnreachableBreachedPasswordChecker : IBreachedPasswordChecker
    {
        public Task<ErrorOr<bool>> IsBreachedAsync(string password, CancellationToken ct) =>
            Task.FromResult<ErrorOr<bool>>(
                Error.Failure(ErrorCodes.ServiceUnavailable, "breached_password_source_unavailable"));
    }
}
