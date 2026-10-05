using ErrorOr;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Account.Legal;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Account.Legal;

public sealed class LegalAcceptanceServiceTests
{
    private static readonly FakeClock Clock = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task EnsureCurrent_NoConsents_Returns409WithBothDocumentsAlphabetical()
    {
        var result = await Service([]).EnsureCurrentAsync(Guid.NewGuid(), CancellationToken.None);
        AssertMissing(result, ["privacy", "tos"]);
    }

    [Fact]
    public async Task EnsureCurrent_OnlyPrivacyMissing_ListsPrivacyOnly()
    {
        var result = await Service([Accepted(LegalDocument.Tos, "1.0.0")])
            .EnsureCurrentAsync(Guid.NewGuid(), CancellationToken.None);
        AssertMissing(result, ["privacy"]);
    }

    [Fact]
    public async Task EnsureCurrent_AcceptedCurrentVersions_Succeeds()
    {
        var result = await Service([Accepted(LegalDocument.Tos, "1.0.0"), Accepted(LegalDocument.Privacy, "1.0.0")])
            .EnsureCurrentAsync(Guid.NewGuid(), CancellationToken.None);
        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureCurrent_StaleAcceptedVersion_Returns409()
    {
        var result = await Service([Accepted(LegalDocument.Tos, "1.0.0"), Accepted(LegalDocument.Privacy, "0.9.0")])
            .EnsureCurrentAsync(Guid.NewGuid(), CancellationToken.None);
        AssertMissing(result, ["privacy"]);
    }

    [Fact]
    public async Task EnsureCurrent_ReadFailure_PropagatesWithoutSuccess()
    {
        var service = new LegalAcceptanceService(new LegalAcceptanceReader(new FailingReader(), new Versions()));
        await Should.ThrowAsync<InvalidOperationException>(() => service.EnsureCurrentAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private static LegalAcceptanceService Service(IReadOnlyList<LegalConsentSnapshot> rows) =>
        new(new LegalAcceptanceReader(new Reader(rows), new Versions()));

    private static LegalConsentSnapshot Accepted(LegalDocument document, string version) => new(document, version, Clock.UtcNow);

    private static void AssertMissing(ErrorOr<Success> result, string[] expected)
    {
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.LegalAcceptanceRequired);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        result.FirstError.Metadata!["documents"].ShouldBeOfType<string[]>().ShouldBe(expected);
    }

    private sealed class Reader(IReadOnlyList<LegalConsentSnapshot> rows) : IConsentReader
    {
        public Task<IReadOnlyList<LegalConsentSnapshot>> LatestAsync(Guid userId, CancellationToken ct, bool includePending = false)
        {
            includePending.ShouldBeFalse("the gate never accepts uncommitted rows");
            return Task.FromResult(rows);
        }
    }

    private sealed class FailingReader : IConsentReader
    {
        public Task<IReadOnlyList<LegalConsentSnapshot>> LatestAsync(Guid userId, CancellationToken ct, bool includePending = false) =>
            throw new InvalidOperationException("database unavailable");
    }

    private sealed class Versions : ICurrentLegalVersions
    {
        public string GetCurrentVersion(LegalDocument document) => "1.0.0";
    }
}
