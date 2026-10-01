using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Domain.Audit;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The <c>audit_logs</c> model as the unit half of the migration's evidence (no database): the index the
/// retention sweep's keyset delete walks ([RULED — README §B10 WT47]) is mapped, and the payload's column
/// is <c>data</c> as plan §3 fixes — the snake_case convention would have derived <c>data_json</c> from the
/// property name. The database itself is proven by the Api integration test of the same name.
/// </summary>
public sealed class AuditLogsMigrationTests
{
    [Fact]
    public void CreatedAtIdIndex_Exists()
    {
        using var context = new AppDbContext(
            ProbeContexts.AppOptions(ProbeContexts.UnusedConnectionString),
            new AnonymousCurrentUser());

        var entity = context.Model.FindEntityType(typeof(AuditLog))!;

        entity.GetTableName().ShouldBe("audit_logs");
        entity.FindProperty(nameof(AuditLog.DataJson))!.GetColumnName().ShouldBe("data");

        var keyset = entity.GetIndexes().Single(index => index.GetDatabaseName() == "ix_audit_logs_created_id");
        keyset.Properties.Select(property => property.Name).ShouldBe(["CreatedAt", "Id"]);

        entity.GetIndexes()
            .Select(index => index.GetDatabaseName())
            .ShouldBe(
                ["ix_audit_logs_created_id", "ix_audit_logs_entity", "ix_audit_logs_subject_created"],
                ignoreOrder: true);
    }
}
