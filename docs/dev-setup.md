# Local development setup

Daily development is native on macOS, without Docker (`plans/00-overview.md` §5.1, `DECISIONS.md` §10.13).

## .NET solution

- SDK 10.0.401 (`global.json`, `rollForward: latestPatch`).
- The two commands below are exactly what `.github/workflows/backend.yml` runs (`build-test`, then
  `integration`). The build-time OpenAPI generator boots the Api, so every build and test command needs
  the four values a deployment always provides: a parseable connection string, which it never uses to
  connect; an ES256 signing key for the start-up validation of 09.3.2-T1 (secret
  `nts/<env>/api/JWT_SIGNING_KEY`); a valid `App:PublicOrigin` for the validation of 09.9.4-T1 (BD12) —
  the base `appsettings.json` carries the Development value `http://localhost:4200`, which is rejected
  outside Development; and the `DP_KEK` PFX of the Data Protection key ring (09.9.4-T1, WF15, secret
  `nts/<env>/shared/DP_KEK`). Generate throwaway values for all of them — CI does the same with
  `openssl`, so no private key or certificate is ever committed.

```bash
export ConnectionStrings__Postgres='Host=127.0.0.1;Database=nighttales;Username=nighttales;Password=ci-openapi-document-generation-placeholder'
export Auth__Jwt__SigningKey="$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256)"
export App__PublicOrigin='https://localhost:4200'   # any valid https origin; never a real deployment one
# A throwaway DP_KEK: an RSA self-signed PFX (RSA because the ring is encrypted with EncryptedXml,
# which accepts RSA only) in a temporary directory, generated once per shell.
export KEK_PASSWORD="$(openssl rand -hex 24)"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=nts-dev-dp-kek' \
  -keyout "${TMPDIR:-/tmp}/dp-kek.key" -out "${TMPDIR:-/tmp}/dp-kek.crt"
openssl pkcs12 -export -out "${TMPDIR:-/tmp}/dp-kek.pfx" \
  -inkey "${TMPDIR:-/tmp}/dp-kek.key" -in "${TMPDIR:-/tmp}/dp-kek.crt" -passout env:KEK_PASSWORD
export DataProtection__KekPfxPath="${TMPDIR:-/tmp}/dp-kek.pfx"
export DataProtection__KekPfxPassword="$KEK_PASSWORD"
dotnet restore NightTales.ci.slnf --locked-mode
dotnet build   NightTales.ci.slnf -c Release --no-restore   # writes src/NightTales.Api/openapi.json (git-ignored)
dotnet format  NightTales.ci.slnf --verify-no-changes --no-restore
```

```bash
# Unit job (no database). The count grows with every feature (98 on the 2026-09-29 wave-2
# tree, 35 when this block was written). `--minimum-expected-tests 1` is the guard that makes a
# zero-match filter red instead of silently green (see the note below). `--no-build` reuses the
# Release build from the block above, so that block (with its ConnectionStrings__Postgres export)
# must have run in this shell.
env -u NTS_TEST_PG dotnet test NightTales.ci.slnf -c Release --no-build \
  --filter "Category!=Integration" --minimum-expected-tests 1
```

```bash
# Integration job (needs the server from the Database section below). The count grows with every
# feature (56 on the 2026-09-29 wave-2 tree, 23 when this block was written). Self-contained: the
# build-time values from the block above plus NTS_TEST_PG are exported here (the same four values the
# first block exports, so re-run that block or export them again). `ConnectionStrings__Postgres`,
# `Auth__Jwt__SigningKey`, `App__PublicOrigin` and the `DP_KEK` pair are required by the build-time
# OpenAPI generator, which boots the Api and runs its start-up validation — not by the tests, which
# read NTS_TEST_PG. Without the connection string a build fails inside the generator: MSB3073 from the
# `dotnet-getdocument` tool that Microsoft.Extensions.ApiDescription.Server.targets invokes, with
# `OptionsValidationException: Configuration value 'ConnectionStrings:Postgres' is required and must
# not be blank`; without the key it fails the same way with `The ES256 signing key (secret
# nts/<env>/api/JWT_SIGNING_KEY) must be the PEM of a P-256 EC private key`, and without the KEK or the
# origin with the fixed `'DataProtection:KekPfxPath' is required` / `'App:PublicOrigin' is required and
# must be an absolute https origin` messages (verified 2026-10-01 — the stack is the whole ASP.NET
# hosting chain, none of it names the missing variable).
export ConnectionStrings__Postgres='Host=127.0.0.1;Database=nighttales;Username=nighttales;Password=ci-openapi-document-generation-placeholder'
export Auth__Jwt__SigningKey="$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256)"
export App__PublicOrigin='https://localhost:4200'
export KEK_PASSWORD="$(openssl rand -hex 24)"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=nts-dev-dp-kek' \
  -keyout "${TMPDIR:-/tmp}/dp-kek.key" -out "${TMPDIR:-/tmp}/dp-kek.crt"
openssl pkcs12 -export -out "${TMPDIR:-/tmp}/dp-kek.pfx" \
  -inkey "${TMPDIR:-/tmp}/dp-kek.key" -in "${TMPDIR:-/tmp}/dp-kek.crt" -passout env:KEK_PASSWORD
export DataProtection__KekPfxPath="${TMPDIR:-/tmp}/dp-kek.pfx"
export DataProtection__KekPfxPassword="$KEK_PASSWORD"
export NTS_TEST_PG="Host=127.0.0.1;Port=5432;Database=${POSTGRES_DB:-nighttales};Username=${POSTGRES_USER:-nighttales};Password=<POSTGRES_PASSWORD from .env>"
dotnet restore NightTales.ci.slnf --locked-mode
dotnet build   NightTales.ci.slnf -c Release --no-restore
dotnet test    NightTales.ci.slnf -c Release --no-build \
  --filter "Category=Integration" --minimum-expected-tests 1
```

- Tests run on Microsoft.Testing.Platform (`global.json` `test.runner`; xunit.v3 4.x no longer supports VSTest on the .NET 10 SDK). Test projects pass `--ignore-exit-code 8` (`tests/Directory.Build.props`), so a run with **zero** tests prints `total: 0` and still exits 0 — a filter typo would pass vacuously. Both commands above therefore pass `--minimum-expected-tests 1`, which fails such a run with `Minimum expected tests policy violation, tests ran 0, minimum expected 1` and exit code 9 (8 would be ignored). Do not add `--logger trx` / `--report-trx`: under this xunit.v3/MTP setup they turn the run into `Zero tests ran` with exit code 5 (verified 2026-09-29); CI uploads the console log instead.
- The Api listens on `http://127.0.0.1:5000` (`src/NightTales.Api/Properties/launchSettings.json`). Use `127.0.0.1`, not `localhost`: macOS AirPlay Receiver (ControlCenter) listens on `*:5000`, and `localhost` can resolve to it.

```bash
# Migration job (09.2.2-T2, the third CI job). It runs the schema harness,
# tests/NightTales.Api.IntegrationTests/Migrations/: `InitialCore` on an empty database, over the
# previous schema with data, the model against its snapshot, and the destructive-operation scan.
# Needs the same server and the same NTS_TEST_PG as the integration job above; the exit-code and
# minimum-tests rules are the same too.
dotnet test NightTales.ci.slnf -c Release --no-build \
  --filter "Category=Migration" --minimum-expected-tests 1
```

The database-backed migration tests also carry `Category=Integration`, so the integration command above
runs them as well; `Category=Migration` is the focused signal for a schema change. The
destructive-operation scan needs no database, so the unit job runs it too.


```bash
# The same build-time environment as the block above (the Api validates `App:PublicOrigin` and the
# DP_KEK on start-up too), plus the connection string it actually uses.
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
| Unit job | needs no `NTS_TEST_PG`: `dotnet test … --filter "Category!=Integration" --minimum-expected-tests 1` leaves out every class tagged `Category=Integration` — every class deriving from `NightTales.TestKit.IntegrationTest` plus the DB-dependent `NightTales.Infrastructure.UnitTests/Persistence` classes |

Each class database's Npgsql pool is cleared before the `DROP DATABASE … WITH (FORCE)`, so the drop does not wait for PostgreSQL to terminate idle pooled backends; that is why a class teardown costs about 10 ms instead of a few hundred.

**Optional compose path (e2e / image smoke only).** `docker-compose.yml` defines a `postgres` service (`postgres:17-alpine`, `127.0.0.1:5432`, healthcheck: a password login with `psql -h 127.0.0.1 ... SELECT 1`). Daily development never starts it. It binds the same port as the native server, so stop one before starting the other, or move the host port in a git-ignored `docker-compose.override.yml`. Its data volume is disposable: `docker compose down -v`. The image applies `POSTGRES_*` only when the volume is first created; after changing them, recreate the volume or the healthcheck stays unhealthy.

## Local mail (Mailpit)

Development sends every e-mail through a local SMTP sink: **Mailpit**, started by `tools/dev/up.sh`
(11.1.1-T2) on `127.0.0.1:1025`, with its UI and its API at <http://127.0.0.1:8025> (09.2.6-T3).

```bash
nc -z 127.0.0.1 1025                            # the SMTP sink every host dials
curl -fsS http://127.0.0.1:8025/api/v1/info     # Mailpit is up; its UI is at the same base URL
```

A message the Api queues is rendered and sent by the Worker's `SendEmailJob` and appears in that UI;
`EmailEndToEndTests` asserts exactly that, and skips itself when the sink is not part of the run. The test
reads two environment variables, which the developer exports before running it (`up.sh` starts the sink; it
does not export them):

```bash
export NTS_MAILPIT_URL=http://127.0.0.1:8025   # the Mailpit API and UI base URL; absent → the test skips
export NTS_SMTP_PORT=1025                     # only when the sink does not listen on the default port
```

Both hosts read the pipeline's configuration from `Email:*`, with the loopback defaults committed in
`appsettings.json`: `Email:Smtp:Host` `127.0.0.1`, `Email:Smtp:Port` `1025`, `Email:Smtp:Security` `None`
(plaintext is accepted only towards a loopback endpoint, in any environment) and `Email:From:Address`
`no-reply@nighttales.local`. The flat `SMTP__HOST`/`SMTP__PORT`/`SMTP__USER`/`SMTP__PASSWORD` names that
11.1.1-T2's `.env` and `up.sh` export are read as an alias and lose to an explicit `Email:Smtp:*` value. A
deployment overrides `Email__Smtp__Host`, `Email__Smtp__Security` (`StartTls`), `Email__Smtp__Username`,
`Email__Smtp__Password` and its own `Email__AddressHashPepper` — the host refuses to start when the
transport is plaintext towards a remote server or the address pepper is blank (09 README BD9/BD16).

## Migrations

`InitialCore` (09.2.2-T2) is the schema every later migration builds on; it is never edited again. A schema
change is an expand-only migration (`NFR-09-6`: expand → migrate → contract) named
`<yyyyMMddHHmm>_<Owner>_<What>`, for example `202610051200_02_SourcesAndFetchTasks` (`InitialCore` itself
keeps EF's timestamp prefix).

```bash
# The tools read the connection string from the environment, never from argv (11 CR-14): a connection
# string in argv is visible to every process on the machine. The design-time factory
# (src/NightTales.Infrastructure/Persistence/AppDbContextDesignFactory.cs) builds the model itself, so the
# Api host is not started and `--startup-project` is not needed (it would need EF's Design package there).
# With the variable unset the factory does not fail: it falls back to the non-secret placeholder
# `Host=invalid.localhost;Database=nts_design_time`, which is never contacted (the factory only builds the
# model). The bundled `nts-migrate` runner takes its own `--connection "<db>"`, applied after the context is
# built, so it runs without this export.
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=5432;Database=${POSTGRES_DB:-nighttales};Username=${POSTGRES_USER:-nighttales};Password=<POSTGRES_PASSWORD from .env>"

dotnet ef migrations add 202610051200_02_SourcesAndFetchTasks \
  --project src/NightTales.Infrastructure --context AppDbContext --output-dir Persistence/Migrations
dotnet ef migrations has-pending-model-changes --project src/NightTales.Infrastructure --context AppDbContext
dotnet ef migrations script --idempotent --project src/NightTales.Infrastructure --context AppDbContext   # read it before the PR
dotnet ef database update --project src/NightTales.Infrastructure --context AppDbContext                 # the local dev database
```

- **Never edit a generated file by hand** except inside `Up`/`Down` of the migration class. The snapshot and the
  Designer are rendered by the generator; `.editorconfig` exempts `Persistence/Migrations/[0-9]*.cs` and
  `AppDbContextModelSnapshot.cs` from the house style rules (their block-scoped namespace, BOM and constant
  array literals are the generator's), so a fresh `migrations add` compiles and formats as it comes out.
- **Two branches that add a migration in parallel** both touch `AppDbContextModelSnapshot.cs` and the Designer of
  the last migration, so the merge conflicts. Do not resolve that conflict by hand: the branch that merges
  **second** removes its own migration (`dotnet ef migrations remove`), merges the other one, and regenerates
  on top. The published migration id of the first branch never changes.
- **A migration that adds a table extends** `tests/NightTales.Api.IntegrationTests/Migrations/SeedPrevious.cs`
  with a row of that table, so the *next* migration is exercised over data, not only over an empty schema.
- **A destructive step** (drop a table/column, narrow a column) needs `[ContractMigration("why the previous
  release is over")]` on the migration class; without it `Migrations_ContainNoDestructiveOperations` fails the
  build, in the unit job and in the `migrations` job. The review checklist is in
  `.github/pull_request_template.md` § Migration.
- The bundle is a build artifact: `dotnet ef migrations bundle --self-contained -r linux-x64 -o
  artifacts/nts-migrate --project src/NightTales.Infrastructure` (`artifacts/` is git-ignored; 11.5.1 builds it
  in the pipeline and runs it as a one-shot job before rollout). Locally it takes the connection string with
  `--connection "<local dev db>"` — applied after the design-time context is built, so the run does not need
  `ConnectionStrings__Postgres` set; in CI and in the cloud the value comes from that variable
  (masked with `::add-mask::`), never from argv.
