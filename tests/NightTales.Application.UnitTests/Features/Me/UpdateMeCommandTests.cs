using ErrorOr;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Features.Me.Commands.UpdateMe;
using NightTales.Domain.Common;
using NightTales.TestKit.Auth;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.AccountProfile;

/// <summary>
/// 09.3.4-T1 unit rules: the validator's three field rules, the audit summary that never carries the
/// display-name text (BD16), and the deleted/missing-account path.
/// </summary>
public sealed class UpdateMeCommandTests
{
    private static readonly UpdateMeValidator Validator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Summary_NeverContainsDisplayNameText()
    {
        var userId = Guid.CreateVersion7();
        var writer = new FakeWriter(new UserProfileUpdate(Profile(userId, "Secret Name"), Language.En, DisplayNameChanged: true));
        var handler = new UpdateMeHandler(new TestCurrentUser { Id = userId }, writer, new NoFlags());
        var command = new UpdateMeCommand("Secret Name", Language.Vi);

        var result = await handler.Handle(command, Ct);

        result.IsError.ShouldBeFalse();
        var summary = command.ToAuditData().Summary;
        summary["fields"].ShouldBe("displayName,uiLocale");
        summary["displayName"].ShouldBe("changed");
        summary["uiLocale"].ShouldBe("en->vi");
        summary.Values.ShouldNotContain(value => value.Contains("Secret", StringComparison.Ordinal));
        writer.LastUserId.ShouldBe(userId, "the handler acts on ICurrentUser.Id only");
    }

    [Fact]
    public async Task Handler_DeletedUser_IsUnauthenticated()
    {
        var userId = Guid.CreateVersion7();
        var handler = new UpdateMeHandler(new TestCurrentUser { Id = userId }, new FakeWriter(null), new NoFlags());

        var result = await handler.Handle(new UpdateMeCommand("Ana", null), Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task Handler_AnonymousCaller_IsUnauthenticated_AndWritesNothing()
    {
        var writer = new FakeWriter(null);
        var handler = new UpdateMeHandler(new TestCurrentUser(), writer, new NoFlags());

        var result = await handler.Handle(new UpdateMeCommand("Ana", null), Ct);

        result.FirstError.Type.ShouldBe(ErrorType.Unauthorized);
        writer.Calls.ShouldBe(0);
    }

    [Fact]
    public void Validator_BothMembersNull_IsRequired()
    {
        var result = Validator.Validate(new UpdateMeCommand(null, null));

        result.Errors.ShouldContain(error => error.ErrorCode == "required" && error.PropertyName == "displayName");
    }

    [Theory]
    [InlineData("Ana")]
    [InlineData("A")]
    public void Validator_ValidDisplayName_Passes(string name) =>
        Validator.Validate(new UpdateMeCommand(name, null)).IsValid.ShouldBeTrue();

    [Fact]
    public void Validator_LocaleOnly_Passes() =>
        Validator.Validate(new UpdateMeCommand(null, Language.En)).IsValid.ShouldBeTrue();

    [Fact]
    public void Validator_EightyChars_Passes_EightyOne_Fails()
    {
        Validator.Validate(new UpdateMeCommand(new string('a', 80), null)).IsValid.ShouldBeTrue();
        Validator.Validate(new UpdateMeCommand(new string('a', 81), null)).Errors
            .ShouldContain(error => error.PropertyName == "displayName");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Ana")]
    [InlineData("Ana ")]
    [InlineData("A\tna")]
    [InlineData("An\na")]
    public void Validator_BadDisplayName_Fails(string name) =>
        Validator.Validate(new UpdateMeCommand(name, null)).Errors
            .ShouldContain(error => error.PropertyName == "displayName");

    [Fact]
    public void Validator_UnknownLocale_Fails() =>
        Validator.Validate(new UpdateMeCommand(null, (Language)99)).Errors
            .ShouldContain(error => error.PropertyName == "uiLocale");

    private static UserProfile Profile(Guid id, string name) =>
        new(id, name, "a@nighttales.test", Language.Vi, PlatformRole.User, MfaEnabled: false, LastLoginAt: null);

    private sealed class FakeWriter(UserProfileUpdate? result) : IUserProfileWriter
    {
        public int Calls { get; private set; }

        public Guid? LastUserId { get; private set; }

        public Task<UserProfileUpdate?> UpdateAsync(Guid userId, string? displayName, Language? uiLocale, CancellationToken ct)
        {
            Calls++;
            LastUserId = userId;

            return Task.FromResult(result);
        }
    }

    private sealed class NoFlags : IFeatureFlags
    {
        public Task<bool> IsEnabledAsync(string key, CancellationToken ct) => Task.FromResult(false);
    }
}
