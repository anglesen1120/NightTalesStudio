using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NightTales.Application.Abstractions;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit;
using NightTales.Infrastructure.UnitTests.TestKit.Probes;
using Shouldly;

namespace NightTales.Infrastructure.UnitTests.Persistence;

public sealed class AppDbContextTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    [Fact]
    public async Task AppDbContext_AggregateUpdate_BumpsXminVersion()
    {
        await using var context = fixture.CreateAppDbContext();
        var aggregate = ProbeAggregate.Create(Guid.CreateVersion7(), DateTimeOffset.UtcNow, ProbeStatus.Draft, "first");

        context.Add(aggregate);
        await context.SaveChangesAsync(CancellationToken.None);

        var inserted = aggregate.Version;
        inserted.ShouldBeGreaterThan(0u, "the insert must read the xmin system column back");

        aggregate.Rename("second");
        await context.SaveChangesAsync(CancellationToken.None);

        aggregate.Version.ShouldNotBe(inserted);
    }

    [Fact]
    public async Task AppDbContext_WriteNonUtcDateTimeOffset_IsRejected()
    {
        await using var context = fixture.CreateAppDbContext();
        var aggregate = ProbeAggregate.Create(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(7)),
            ProbeStatus.Draft,
            "non-utc");

        context.Add(aggregate);

        var exception = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(CancellationToken.None));

        var innermost = Innermost(exception);
        innermost.Message.ShouldContain("offset", Case.Insensitive);
        innermost.Message.ShouldContain("timestamp with time zone", Case.Insensitive);
    }

    [Fact]
    public void Model_ColumnsAreSnakeCase_InInformationSchema()
    {
        using var context = fixture.CreateAppDbContext();

        var columns = TestSql.QueryStrings(
            context,
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'probe_aggregates'");

        columns.ShouldContain("created_at");
        columns.ShouldNotContain("CreatedAt");
    }

    [Fact]
    public void Model_IdentityTables_AreRenamed()
    {
        using var context = fixture.CreateAppDbContext();

        var tables = TestSql.QueryStrings(
            context,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'");

        tables.ShouldContain("users");
        tables.ShouldContain("roles");
        tables.ShouldContain("user_roles");
        tables.ShouldContain("user_claims");
        tables.ShouldContain("user_logins");
        tables.ShouldContain("user_tokens");
        tables.ShouldContain("role_claims");

        tables.ShouldNotContain("AspNetUsers");
    }

    [Fact]
    public void Model_AggregateRootVersion_MapsToXminSystemColumn()
    {
        using var context = new AppDbContext(ProbeContexts.AppOptions(ProbeContexts.UnusedConnectionString), new AnonymousCurrentUser());

        var version = context.Model.FindEntityType(typeof(ProbeAggregate))!.FindProperty(nameof(AggregateRoot.Version))!;

        version.ValueGenerated.ShouldBe(ValueGenerated.OnAddOrUpdate);
        version.IsConcurrencyToken.ShouldBeTrue();
        version.GetColumnName().ShouldBe("xmin");
    }

    [Fact]
    public void Model_EntityId_IsNeverGenerated()
    {
        using var context = new AppDbContext(ProbeContexts.AppOptions(ProbeContexts.UnusedConnectionString), new AnonymousCurrentUser());

        var id = context.Model.FindEntityType(typeof(ProbeAggregate))!.FindProperty(nameof(Entity.Id))!;

        id.ValueGenerated.ShouldBe(ValueGenerated.Never);
    }

    [Fact]
    public void Model_ReadAndWriteContexts_MapTheSameModel()
    {
        using var write = new AppDbContext(ProbeContexts.AppOptions(ProbeContexts.UnusedConnectionString), new AnonymousCurrentUser());
        using var read = new ReadDbContext(ProbeContexts.ReadOptions(ProbeContexts.UnusedConnectionString), new AnonymousCurrentUser());

        Shape(read.Model).ShouldBe(Shape(write.Model));
    }

    private static List<string> Shape(IModel model)
        => [.. model.GetEntityTypes()
            .Select(entity => $"{entity.ClrType.Name}->{entity.GetTableName()}:{string.Join(",", entity.GetProperties().Select(property => property.GetColumnName()))}")
            .Order(StringComparer.Ordinal)];

    private static Exception Innermost(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }
}
