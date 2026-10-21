using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// <c>ConsentWriter</c> against a real PostgreSQL 17 (09.3.1-T1 Tests table): a consent is an
/// acceptance <em>history</em> (13 FR-13-2, two rows for the same document), it records version and
/// caller address, and it is committed only when the caller's transaction commits.
/// </summary>
public sealed class ConsentWriterTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task RecordAsync_WritesDocumentVersionAndIp()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IConsentWriter>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(ct);
        (await writer.RecordAsync(user.Id, LegalDocument.Tos, "v1", IPAddress.Parse("203.0.113.7"), ct))
            .IsError.ShouldBeFalse();
        (await writer.RecordAsync(user.Id, LegalDocument.Tos, "v2", ip: null, ct))
            .IsError.ShouldBeFalse();
        (await unitOfWork.CommitAsync(ct)).IsError.ShouldBeFalse();

        var rows = await Database.QueryStringsAsync(
            $"SELECT document || '|' || version || '|' || coalesce(host(ip), '-') "
            + $"|| '|' || (accepted_at = '{FakeClock.DefaultStart:O}'::timestamptz)::text FROM user_consents "
            + $"WHERE user_id = '{user.Id}' ORDER BY version",
            ct);

        rows.ShouldBe(
            ["tos|v1|203.0.113.7|true", "tos|v2|-|true"],
            "the same (user, document) twice is two rows (acceptance history), "
            + "and accepted_at is the scope's clock, not the database clock");
    }

    [Fact]
    public async Task RecordAsync_IsCommittedWithTheCallersTransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IConsentWriter>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(ct);
        (await writer.RecordAsync(user.Id, LegalDocument.Privacy, "v1", ip: null, ct)).IsError.ShouldBeFalse();

        // Another connection, i.e. every other reader: the row does not exist until the caller commits.
        (await CountConsentsAsync(user.Id, ct)).ShouldBe(0, "an uncommitted consent is invisible outside the transaction");

        await unitOfWork.RollbackAsync(ct);

        (await CountConsentsAsync(user.Id, ct)).ShouldBe(0, "a handler that fails (rollback) leaves no consent row");
    }

    [Fact]
    public async Task RecordAsync_BlankVersion_IsRefusedBeforeTheDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IConsentWriter>();

        await Should.ThrowAsync<ArgumentException>(
            async () => await writer.RecordAsync(Guid.Empty, LegalDocument.Tos, " ", ip: null, ct));
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await writer.RecordAsync(Guid.Empty, LegalDocument.Tos, null!, ip: null, ct));
    }

    [Fact]
    public async Task RecordAsync_VersionOver32Characters_IsRefusedBeforeTheDatabase()
    {
        // user_consents.version is character varying(32); the writer fails fast on a value the column
        // would reject, so the refusal is the domain rule and not SQLSTATE 22001.
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IConsentWriter>();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await writer.RecordAsync(
                Guid.Empty, LegalDocument.Privacy, new string('v', 33), ip: null, ct));
    }

    [Fact]
    public async Task CommitAsync_ForeignKeyViolation_MapsToValidationError_NeverThrows()
    {
        // 09.3.1-T1 failure handling: a user_consents FK violation (the user deleted between steps)
        // must surface as 400 validation_failed, never a raw 500. The writer records against a user
        // that does not exist, so the commit hits the ON DELETE RESTRICT foreign key (SQLSTATE 23503).
        var ct = TestContext.Current.CancellationToken;

        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IConsentWriter>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(ct);
        (await writer.RecordAsync(Guid.CreateVersion7(), LegalDocument.Tos, "v1", ip: null, ct))
            .IsError.ShouldBeFalse("the writer only stages the row; the constraint fires at commit");

        var commit = await unitOfWork.CommitAsync(ct);

        commit.IsError.ShouldBeTrue("a refused commit is an error, never a thrown DbUpdateException");
        commit.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
    }

    private async Task<int> CountConsentsAsync(Guid userId, CancellationToken ct) =>
        (await Database.QueryStringsAsync($"SELECT id::text FROM user_consents WHERE user_id = '{userId}'", ct)).Count;
}
