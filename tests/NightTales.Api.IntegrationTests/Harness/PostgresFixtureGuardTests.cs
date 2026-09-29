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
        var longest = $"nts_{new string('a', 59)}";
        longest.Length.ShouldBe(63);
        PostgresFixture.QuoteIdentifier(longest).ShouldBe($"\"{longest}\"");
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

    [Fact]
    public void PostgresFixture_UnmanagedDatabaseName_IsRefusedBeforeAnyDdl()
    {
        // SEC 09.2.1-T2: the harness drops and creates databases, so only names carrying the
        // reserved prefix may be rendered into DDL — a typo must fail before the server sees it.
        foreach (var unmanaged in new[] { "nighttales", "postgres", "template1", "ntsfoo" })
        {
            var exception = Should.Throw<InvalidOperationException>(
                () => PostgresFixture.RequireManagedDatabaseName(unmanaged));

            exception.Message.ShouldContain(PostgresFixture.ManagedDatabasePrefix);
            exception.Message.ShouldContain(PostgresFixture.ConnectionStringVariable);
            exception.Message.ShouldNotContain("DROP DATABASE x");

            // The DDL choke point enforces the same rule.
            Should.Throw<InvalidOperationException>(() => PostgresFixture.QuoteIdentifier(unmanaged));
        }

        // The fixture refuses an unmanaged template name at construction, not at first use.
        var templateException = Should.Throw<InvalidOperationException>(() => new PostgresFixture("nighttales"));
        templateException.Message.ShouldContain(PostgresFixture.ManagedDatabasePrefix);

        PostgresFixture.RequireManagedDatabaseName("nts_ok").ShouldBe("nts_ok");
        PostgresFixture.QuoteIdentifier("nts_t_abc123").ShouldBe("\"nts_t_abc123\"");
    }

    [Fact]
    public void PostgresFixture_RemoteDdlTarget_IsRefusedUnlessOverrideIsSet()
    {
        // SEC 09.2.1-T2: the fixture runs destructive DDL, so only this machine is accepted unless
        // the owner opts in explicitly (and never for a shared or production cluster).
        foreach (var loopback in new[]
        {
            "Host=127.0.0.1;Port=5432;Database=nighttales;Username=nts",
            // The value the CI integration job uses (09.1.5-T1): the postgres:17 service container
            // reached on loopback, with no override set (SEC 09.2.1-T2).
            "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres",
            "Host=localhost;Port=5432;Database=nighttales;Username=nts",
            "Host=127.0.0.5;Port=5432;Database=nighttales;Username=nts",
            "Host=::1;Port=5432;Database=nighttales;Username=nts",
            "Host=[::1];Port=5432;Database=nighttales;Username=nts",
            "Host=/tmp;Port=5432;Database=nighttales;Username=nts",
            "Host=127.0.0.1,localhost;Port=5432;Database=nighttales;Username=nts",
            "Port=5432;Database=nighttales;Username=nts",
        })
        {
            Should.NotThrow(() => PostgresFixture.EnsureDdlTargetIsLoopback(loopback, allowRemote: false));
        }

        foreach (var remote in new[]
        {
            "Host=db.example.com;Port=5432;Database=nighttales;Username=nts",
            "Host=10.0.0.5;Port=5432;Database=nighttales;Username=nts",
            "Host=127.0.0.1,db.internal;Port=5432;Database=nighttales;Username=nts",
        })
        {
            var exception = Should.Throw<InvalidOperationException>(
                () => PostgresFixture.EnsureDdlTargetIsLoopback(remote, allowRemote: false));

            exception.Message.ShouldContain(PostgresFixture.ConnectionStringVariable);
            exception.Message.ShouldContain(PostgresFixture.AllowRemoteDdlVariable);
            exception.Message.ShouldContain("drops and creates databases");

            // The documented boundary: the override is the only way in, and it must be deliberate.
            Should.NotThrow(() => PostgresFixture.EnsureDdlTargetIsLoopback(remote, allowRemote: true));
        }
    }

    [Fact]
    public void PostgresFixture_LoopbackHostWithPort_IsAccepted()
    {
        // L-residual (09.2.1-T2 delta re-review): Npgsql allows a per-host port, and a port never
        // changes where the connection goes, so `127.0.0.1:5432` must not be refused as remote.
        foreach (var loopback in new[]
        {
            "Host=127.0.0.1:5432;Database=nighttales;Username=nts",
            "Host=localhost:5432;Database=nighttales;Username=nts",
            "Host=[::1]:5432;Database=nighttales;Username=nts",
            "Host=::1;Database=nighttales;Username=nts",
            "Host=127.0.0.1;Port=5433;Database=nighttales;Username=nts",
            "Host=127.0.0.1:5432,localhost:5433;Database=nighttales;Username=nts",
        })
        {
            Should.NotThrow(() => PostgresFixture.EnsureDdlTargetIsLoopback(loopback, allowRemote: false));
        }

        // A port does not make a remote host local, and a `:` suffix that is not a plain port number
        // is not a host literal this guard can verify, so both are refused (documented choice).
        foreach (var refused in new[]
        {
            "Host=10.0.0.5:5432;Database=nighttales;Username=nts",
            "Host=127.0.0.1:notaport;Database=nighttales;Username=nts",
            "Host=127.0.0.1:;Database=nighttales;Username=nts",
        })
        {
            Should.Throw<InvalidOperationException>(
                () => PostgresFixture.EnsureDdlTargetIsLoopback(refused, allowRemote: false));
            Should.NotThrow(() => PostgresFixture.EnsureDdlTargetIsLoopback(refused, allowRemote: true));
        }
    }

    [Fact]
    public async Task PostgresFixture_NonManagedDatabase_RawSqlHelpers_AreRefused()
    {
        // L-residual (09.2.1-T2 delta re-review): the server-level statement runner is private now and
        // every per-database helper validates the managed prefix, so no public member can run SQL
        // against a database the harness does not own. The refusal happens before any connection, so
        // the unit job needs no NTS_TEST_PG.
        var fixture = new PostgresFixture();

        Should.Throw<InvalidOperationException>(() => fixture.ConnectionStringFor("nighttales"));
        Should.Throw<InvalidOperationException>(() => fixture.ConnectionStringFor("postgres"));

        await Should.ThrowAsync<InvalidOperationException>(() => fixture.ExecuteAsync("nighttales", "SELECT 1"));
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.QueryStringsAsync("public", "SELECT 1"));
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.TableNamesAsync("template1"));
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.AppliedMigrationsAsync("nighttales"));
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.PendingMigrationsAsync("nighttales"));

        // DatabaseExistsAsync / DatabaseOidAsync stay name-agnostic on purpose: they are read-only,
        // parameterized catalog probes on the maintenance connection, not a SQL entry point. Every
        // refusal above happens before the connection string is read, so the unit job needs no
        // NTS_TEST_PG; the accepted managed path is covered by every integration test (TestDatabase's
        // constructor resolves its own nts_t_… name through ConnectionStringFor) and by
        // PostgresFixture_UnmanagedDatabaseName_IsRefusedBeforeAnyDdl.
    }

    [Fact]
    public void PostgresFixture_RemoteDdlTarget_RefusalIsWiredIntoTheEnvGuard()
    {
        // SEC 09.2.1-T2: RequireServerConnectionString is the only entry point of the fixture, so it
        // must apply the loopback rule with the environment override. The override is unset for every
        // normal run (dev and CI both use a loopback host), but the assertion stays valid either way.
        const string Remote = "Host=db.example.com;Port=5432;Database=nighttales;Username=nts;Password=nts";

        if (PostgresFixture.IsRemoteDdlAllowed())
        {
            PostgresFixture.RequireServerConnectionString(Remote).ShouldBe(Remote);
        }
        else
        {
            Should.Throw<InvalidOperationException>(() => PostgresFixture.RequireServerConnectionString(Remote))
                .Message.ShouldContain(PostgresFixture.AllowRemoteDdlVariable);
        }

        // The dev and CI value is a loopback host, so it is always accepted.
        const string Loopback = "Host=127.0.0.1;Port=5432;Database=nighttales;Username=nighttales;Password=nts";
        PostgresFixture.RequireServerConnectionString(Loopback).ShouldBe(Loopback);
    }

    [Fact]
    public void PostgresFixture_DdlPrivilegeFailure_NamesStatementAndDbInitScript()
    {
        // SEC 09.2.1-T2: a role without CREATEDB / without ownership used to surface the raw
        // Npgsql.PostgresException (SQLSTATE 42501). The DDL path now maps that one SQLSTATE to a
        // named error; every other SQLSTATE keeps its original exception.
        var create = PostgresFixture.DescribeDdlFailure("42501", PostgresFixture.DdlStatement.CreateDatabase);
        create.ShouldNotBeNull();
        create.ShouldContain(PostgresFixture.ConnectionStringVariable);
        create.ShouldContain("CREATE DATABASE");
        create.ShouldContain("tools/dev/db-init.sh");
        create.ShouldContain("42501");

        var drop = PostgresFixture.DescribeDdlFailure("42501", PostgresFixture.DdlStatement.DropDatabase);
        drop.ShouldNotBeNull();
        drop.ShouldContain("DROP DATABASE");
        drop.ShouldContain("tools/dev/db-init.sh");

        // Anything that is not the privilege failure is not this mapping's business.
        PostgresFixture.DescribeDdlFailure("42P04", PostgresFixture.DdlStatement.CreateDatabase).ShouldBeNull();
        PostgresFixture.DescribeDdlFailure("3D000", PostgresFixture.DdlStatement.DropDatabase).ShouldBeNull();
        PostgresFixture.DescribeDdlFailure(null, PostgresFixture.DdlStatement.DropDatabase).ShouldBeNull();
    }
}
