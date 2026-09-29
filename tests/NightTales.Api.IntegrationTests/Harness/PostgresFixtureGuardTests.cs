using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// The two fixture guards are pure functions, so these are unit tests: no server, no
/// <c>Category=Integration</c> trait, and they run in the unit job without <c>NTS_TEST_PG</c>
/// (09 README D13: fail fast with a named error, never skip).
/// </summary>
public sealed class PostgresFixtureGuardTests
{
    [Fact]
    public void PostgresFixture_MissingEnv_FailsWithNamedError()
    {
        foreach (var blank in new[] { null, string.Empty, "   " })
        {
            var exception = Should.Throw<InvalidOperationException>(
                () => PostgresFixture.RequireServerConnectionString(blank));

            exception.Message.ShouldContain(PostgresFixture.ConnectionStringVariable);
            exception.Message.ShouldContain("PostgreSQL 17");
            exception.Message.ShouldContain("docs/dev-setup.md");
        }
    }

    [Fact]
    public void PostgresFixture_ServerVersionNot17_Fails()
    {
        foreach (var (versionNum, version) in new[] { ("160009", "16.9"), ("180000", "18.0") })
        {
            var exception = Should.Throw<InvalidOperationException>(
                () => PostgresFixture.EnsureServerMajor17(versionNum, version));

            exception.Message.ShouldContain(PostgresFixture.ConnectionStringVariable);
            exception.Message.ShouldContain("PostgreSQL 17");
            exception.Message.ShouldContain(version);
        }

        Should.NotThrow(() => PostgresFixture.EnsureServerMajor17("170006", "17.6"));
    }

    [Fact]
    public void PostgresFixture_UnparsableEnv_FailsWithoutEchoingThePassword()
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => PostgresFixture.RequireServerConnectionString("Host=127.0.0.1;Password=sup3r-s3cret;Zzz=1"));

        exception.Message.ShouldContain(PostgresFixture.ConnectionStringVariable);
        exception.Message.ShouldNotContain("sup3r-s3cret");
    }

    [Fact]
    public void PostgresFixture_ServerVersionBoundaries_AreHandled()
    {
        // QA 09.2.1-T2: boundary values around the major-17 cut — 16.99 and 18-dev must fail, and a
        // non-numeric server_version_num must fail with the named error instead of parsing to 0.
        foreach (var (versionNum, version) in new[]
        {
            ("169999", "16.99"),
            ("180001", "18.0beta1"),
            ("not-a-number", "weird"),
        })
        {
            var exception = Should.Throw<InvalidOperationException>(
                () => PostgresFixture.EnsureServerMajor17(versionNum, version));

            exception.Message.ShouldContain(PostgresFixture.ConnectionStringVariable);
            exception.Message.ShouldContain("PostgreSQL 17");
        }

        Should.NotThrow(() => PostgresFixture.EnsureServerMajor17("170000", "17.0"));
    }

    [Fact]
    public void PostgresFixture_QuoteIdentifier_RejectsUnsafeNames()
    {
        // QA 09.2.1-T2: database names are interpolated into DDL that cannot take parameters, so
        // anything outside [a-z0-9_] (max 63 chars) must be rejected before it reaches the server.
        foreach (var bad in new[]
        {
            "Upper", "with-dash", "with space", "inj\";DROP DATABASE x;--",
            "semi;colon", string.Empty, "   ", new string('a', 64),
        })
        {
            Should.Throw<ArgumentException>(() => PostgresFixture.QuoteIdentifier(bad));
        }

        PostgresFixture.QuoteIdentifier("nts_t_abc123").ShouldBe("\"nts_t_abc123\"");
        PostgresFixture.QuoteIdentifier(new string('a', 63)).ShouldBe($"\"{new string('a', 63)}\"");
    }

    [Fact]
    public void PostgresFixture_MaskedConnectionString_HidesThePassword()
    {
        var masked = PostgresFixture.MaskPassword(
            "Host=127.0.0.1;Port=5432;Database=nighttales;Username=nighttales;Password=sup3r-s3cret");

        masked.ShouldNotContain("sup3r-s3cret");
        masked.ShouldContain("127.0.0.1");
        masked.ShouldContain("nighttales");
    }
}
