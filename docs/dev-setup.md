# Local development setup

Daily development is native on macOS, without Docker (`plans/00-overview.md` §5.1, `DECISIONS.md` §10.13).

## .NET solution

- SDK 10.0.401 (`global.json`, `rollForward: latestPatch`).
- Build and test:

```bash
dotnet restore NightTales.ci.slnf --locked-mode
dotnet build   NightTales.ci.slnf -c Release --no-restore
dotnet test    NightTales.ci.slnf -c Release --no-build
```

- Tests run on Microsoft.Testing.Platform (`global.json` `test.runner`; xunit.v3 4.x no longer supports VSTest on the .NET 10 SDK). Test projects pass `--ignore-exit-code 8` (`tests/Directory.Build.props`), so a project with zero tests does not fail the run.
- The Api listens on `http://127.0.0.1:5000` (`src/NightTales.Api/Properties/launchSettings.json`). Use `127.0.0.1`, not `localhost`: macOS AirPlay Receiver (ControlCenter) listens on `*:5000`, and `localhost` can resolve to it.

```bash
dotnet run --project src/NightTales.Api
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5000/health/live   # 200
```

## Database

Native PostgreSQL 17 from Homebrew on `127.0.0.1:5432` (loopback only).

```bash
(umask 077; cp .env.example .env)        # then set your own POSTGRES_PASSWORD (unquoted)
brew install postgresql@17
brew services start postgresql@17
tools/dev/db-init.sh                      # role + database from .env; safe to re-run
export PATH="$(brew --prefix postgresql@17)/bin:$PATH"   # psql / pg_isready (keg-only formula)
```

`db-init.sh` first checks (and re-checks after its own restart) that the server on `127.0.0.1:5432` is Homebrew `postgresql@17` (`server_version_num` 17.x, `data_directory` = `$(brew --prefix)/var/postgresql@17`); otherwise it exits 1 without changing anything. It then sets `listen_addresses = '127.0.0.1'` (restarting the service once if it differs), creates the `.env` role with `LOGIN CREATEDB` and its database, and converges the password and the database owner on re-runs. It refuses `postgres`/`pg_*` and any existing role with SUPERUSER, CREATEROLE, REPLICATION or BYPASSRLS, the system databases, a missing `.env` password and the `.env.example` password. `CREATEDB` is needed by the test fixture, which creates one database per test class. Homebrew's default `pg_hba.conf` trusts `127.0.0.1`, so locally the password is not enforced; it matters for the compose path and for any `scram-sha-256` setup.

Connection string for the Api and the Worker, never in `appsettings*.json`. `db-init.sh --user-secrets` stores it with `dotnet user-secrets` for both projects, built from the same parsed `.env` values and piped as JSON on stdin, so the password never appears in argv or shell history:

```bash
tools/dev/db-init.sh --user-secrets
```

The environment variable `ConnectionStrings__Postgres` works as well. Integration tests read `NTS_TEST_PG`, which points at this same server; export it (or set it in the shell that runs `dotnet test`) with the same `.env` values and a database the role can connect to, because each test class creates and drops its own database next to it:

```bash
export NTS_TEST_PG="Host=127.0.0.1;Port=5432;Database=${POSTGRES_DB:-nighttales};Username=${POSTGRES_USER:-nighttales};Password=<POSTGRES_PASSWORD from .env>"
```

The `Database=` value is only where the fixture runs its own DDL — `CREATE DATABASE`, `DROP DATABASE` and the catalog queries. No test ever writes into it: every test class gets its own fresh `nts_t_<uuid>` database copied from the template, so the value carries no state and **any database the role can connect to works**. The dev value above is `nighttales` (the `.env` database made connectable by `tools/dev/db-init.sh`); the CI integration job points `NTS_TEST_PG` at its `postgres:17` service container, which it reaches at `127.0.0.1` — a loopback host, so CI needs no override.

A missing or blank `NTS_TEST_PG` fails the test fixture with a named error; it never skips silently. The fixture checks the server once per run (`tests/NightTales.TestKit/Database/PostgresFixture.cs`):

| Fact | Behaviour |
|---|---|
| Server major | must be PostgreSQL **17** (`server_version_num / 10000`); anything else fails with `NTS_TEST_PG must point at PostgreSQL 17 (found …)` before a database is created |
| Role privileges | the role needs `CREATEDB` and must own the databases the harness manages: `PostgresFixture` drops and rebuilds the template database `nts_template` once per run and each test class gets `CREATE DATABASE … TEMPLATE nts_template`, dropped with `FORCE` when the class is over. SQLSTATE `42501` (for example `must be owner of database nts_template` or `permission denied to create database`) becomes an `InvalidOperationException` that names the failing statement and points at `tools/dev/db-init.sh`; every other SQLSTATE keeps its original `PostgresException` |
| Managed database names | every database the fixture creates or drops must start with the reserved prefix `nts_` — the template name is checked when the fixture is constructed, and the DDL renderer refuses any other name, so a typo or a stray name can never reach a shared database |
| DDL target host | the server must be **loopback** (`127.0.0.0/8`, `::1`, `localhost` or a Unix-socket path). A remote host is refused with `NTS_TEST_PG points at '<host>', which is not a loopback host …` unless `NTS_TEST_PG_ALLOW_REMOTE_DDL=1` is set explicitly; the fixture **drops and creates databases**, so that override must never target a shared or production cluster — only a disposable test server. CI needs no override: the `postgres:17` service container is reached at `127.0.0.1` (loopback) |
| Missing, blank or unparsable value | `InvalidOperationException` naming `NTS_TEST_PG`, with the connection string masked (`Password=***`); never a skip |
| Unit job | needs no `NTS_TEST_PG`: `dotnet test … --filter-not-trait "Category=Integration"` leaves out every class that derives from `NightTales.TestKit.IntegrationTest` |

Each class database's Npgsql pool is cleared before the `DROP DATABASE … WITH (FORCE)`, so the drop does not wait for PostgreSQL to terminate idle pooled backends; that is why a class teardown costs about 10 ms instead of a few hundred.

**Optional compose path (e2e / image smoke only).** `docker-compose.yml` defines a `postgres` service (`postgres:17-alpine`, `127.0.0.1:5432`, healthcheck: a password login with `psql -h 127.0.0.1 ... SELECT 1`). Daily development never starts it. It binds the same port as the native server, so stop one before starting the other, or move the host port in a git-ignored `docker-compose.override.yml`. Its data volume is disposable: `docker compose down -v`. The image applies `POSTGRES_*` only when the volume is first created; after changing them, recreate the volume or the healthcheck stays unhealthy.
