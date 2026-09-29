using Xunit;
using NightTales.Infrastructure.UnitTests.TestKit;
using NightTales.Infrastructure.UnitTests.TestKit.Probes;
using Shouldly;

namespace NightTales.Infrastructure.UnitTests.Persistence;

public sealed class ModelConstraintTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    [Fact]
    public void Model_EnumColumn_RejectsUnknownValue()
    {
        using var context = fixture.CreateAppDbContext();

        var sqlState = TestSql.SqlStateOf(
            context,
            """
            INSERT INTO probe_aggregates (id, user_id, created_at, status, name, settings)
            VALUES (gen_random_uuid(), gen_random_uuid(), now(), 'bogus', 'x', '{"Tone":"calm","Tags":[]}'::jsonb)
            """);

        sqlState.ShouldBe("23514");
    }

    [Fact]
    public void Model_EnumColumn_HasNamedCheckConstraint()
    {
        using var context = fixture.CreateAppDbContext();

        var constraints = TestSql.QueryStrings(
            context,
            "SELECT conname FROM pg_constraint WHERE conrelid = 'probe_aggregates'::regclass AND contype = 'c'");

        constraints.ShouldContain("ck_probe_aggregates_status");
    }

    [Fact]
    public void Model_EnumColumn_AcceptsSnakeCaseValue()
    {
        using var context = fixture.CreateAppDbContext();

        var inserted = TestSql.QueryStrings(
            context,
            """
            INSERT INTO probe_aggregates (id, user_id, created_at, status, name, settings)
            VALUES (gen_random_uuid(), gen_random_uuid(), now(), 'in_progress', 'accepted', '{"Tone":"calm","Tags":[]}'::jsonb)
            RETURNING status
            """);

        inserted.ShouldHaveSingleItem().ShouldBe("in_progress");

        var created = context.Set<ProbeAggregate>().Single(aggregate => aggregate.Name == "accepted");
        created.Status.ShouldBe(ProbeStatus.InProgress);
    }
}
