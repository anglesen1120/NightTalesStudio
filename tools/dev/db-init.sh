#!/bin/bash
# tools/dev/db-init.sh - create the local dev role and database on the native
# Homebrew postgresql@17 server (09.1.1-T2, 09 W3 CR-35). Idempotent.
# Exit codes: 0 ok, 1 check failed, 2 usage / environment.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: tools/dev/db-init.sh [--user-secrets] [--help]

Creates (or converges) the role and database named in .env on the native
postgresql@17 server at 127.0.0.1:5432:
  - listen_addresses = '127.0.0.1' (restarts the Homebrew service once if changed)
  - role POSTGRES_USER with LOGIN, CREATEDB and POSTGRES_PASSWORD
  - database POSTGRES_DB owned by that role
POSTGRES_DB / POSTGRES_USER come from .env, falling back to .env.example;
POSTGRES_PASSWORD comes only from .env and must not be the example value.
The server must be Homebrew postgresql@17 (version 17, its own data directory).
Re-running changes nothing.
--user-secrets also stores ConnectionStrings:Postgres for the Api and the Worker
(dotnet user-secrets, JSON on stdin; the password never reaches argv).
EOF
}

set_user_secrets=0
case "${1:-}" in
  -h|--help) usage; exit 0 ;;
  --user-secrets) set_user_secrets=1 ;;
  "") ;;
  *) echo "db-init.sh: unknown argument: $1 (see --help)" >&2; exit 2 ;;
esac
[ "$#" -le 1 ] || { echo "db-init.sh: too many arguments (see --help)" >&2; exit 2; }

log() { printf '%s db-init.sh: %s\n' "$(date -u +%FT%TZ)" "$*" >&2; }
die() { local code="$1"; shift; log "ERROR: $*"; exit "$code"; }

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"

# Shared helpers; nts_pg_assert_homebrew_cluster is the version/data_directory check down.sh
# --volumes also runs before it drops anything.
# shellcheck source=tools/dev/lib.sh
. "$repo_root/tools/dev/lib.sh"

# Reads KEY=VALUE from a dotenv file without sourcing it (no code execution).
env_value() {
  local file="$1" key="$2" line value
  [ -f "$file" ] || return 1
  line="$(grep -E "^[[:space:]]*${key}=" "$file" | tail -n 1)" || return 1
  value="${line#*=}"
  value="${value%$'\r'}"
  local dq='^"([^"]*)"[[:space:]]*(#.*)?$' sq="^'([^']*)'[[:space:]]*(#.*)?\$"
  case "$value" in
    \"*|\'*)
      # Quoted: the closing quote may be followed only by blanks or a comment; no escapes.
      if [[ "$value" =~ $dq ]] || [[ "$value" =~ $sq ]]; then
        value="${BASH_REMATCH[1]}"
      else
        log "ERROR: malformed quoted value for $key in $file"
        return 2
      fi
      ;;
    *)
      # Unquoted: drop an inline " #" comment and trailing blanks (docker compose dotenv rules).
      value="${value%%[[:space:]]#*}"
      value="${value%"${value##*[![:space:]]}"}"
      ;;
  esac
  printf '%s' "$value"
}

# setting KEY [FILE...]: first non-empty value; exit 2 on a malformed line.
setting() {
  local key="$1" f v rc
  shift
  for f in "$@"; do
    rc=0
    v="$(env_value "$f" "$key")" || rc=$?
    [ "$rc" -ne 2 ] || exit 2
    if [ "$rc" -eq 0 ] && [ -n "$v" ]; then printf '%s' "$v"; return 0; fi
  done
  return 1
}

env_file="$repo_root/.env"
example_file="$repo_root/.env.example"
db_name="$(setting POSTGRES_DB "$env_file" "$example_file")" || die 2 "POSTGRES_DB not set in .env or .env.example"
db_user="$(setting POSTGRES_USER "$env_file" "$example_file")" || die 2 "POSTGRES_USER not set in .env or .env.example"
db_password="$(setting POSTGRES_PASSWORD "$env_file")" \
  || die 2 "POSTGRES_PASSWORD not set in .env (cp .env.example .env, chmod 600 .env, choose a password)"
rc=0
example_password="$(setting POSTGRES_PASSWORD "$example_file")" || rc=$?
[ "$rc" -ne 2 ] || exit 2
[ "$rc" -eq 0 ] || example_password=""
[ "$db_password" != "$example_password" ] || die 2 "POSTGRES_PASSWORD in .env is still the .env.example value; choose your own"

ident_re='^[a-z_][a-z0-9_]{0,62}$'
[[ "$db_name" =~ $ident_re ]] || die 2 "POSTGRES_DB must match $ident_re"
[[ "$db_user" =~ $ident_re ]] || die 2 "POSTGRES_USER must match $ident_re"
case "$db_user" in
  postgres|pg_*) die 2 "POSTGRES_USER must not be a built-in role ('postgres' or 'pg_*')" ;;
esac
case "$db_name" in
  postgres|template0|template1) die 2 "POSTGRES_DB must not be a system database ('$db_name')" ;;
esac

command -v brew >/dev/null 2>&1 || die 2 "Homebrew not found (brew install postgresql@17)"
pg_prefix="$(brew --prefix postgresql@17 2>/dev/null)" || die 2 "postgresql@17 not installed: brew install postgresql@17"
pg_bin="$pg_prefix/bin"
[ -x "$pg_bin/psql" ] || die 2 "postgresql@17 not installed: brew install postgresql@17"
brew_root="$(brew --prefix)" || die 2 "brew --prefix failed"
expected_datadir="$brew_root/var/postgresql@17"

pg_isready_ok() { "$pg_bin/pg_isready" -q -h 127.0.0.1 -p 5432; }

psql_admin() {
  "$pg_bin/psql" -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 5432 -d postgres "$@"
}

pg_isready_ok || die 1 "PostgreSQL is not accepting connections on 127.0.0.1:5432; run: brew services start postgresql@17"

# Nothing is changed and no password is sent until the listener is this Homebrew cluster.
nts_pg_assert_homebrew_cluster "$pg_bin" "$expected_datadir"

listen="$(psql_admin -At -c 'SHOW listen_addresses')" || die 1 "cannot read listen_addresses"
if [ "$listen" != "127.0.0.1" ]; then
  log "listen_addresses is '$listen'; setting '127.0.0.1' and restarting postgresql@17"
  psql_admin -c "ALTER SYSTEM SET listen_addresses = '127.0.0.1'" || die 1 "ALTER SYSTEM SET listen_addresses failed"
  brew services restart postgresql@17 >/dev/null || die 1 "brew services restart postgresql@17 failed"
  i=0
  until pg_isready_ok; do
    i=$((i + 1))
    [ "$i" -le 30 ] || die 1 "postgresql@17 did not come back within 30 s after the restart"
    sleep 1
  done
  # The restart opened a window in which another process could take the port.
  nts_pg_assert_homebrew_cluster "$pg_bin" "$expected_datadir"
fi

# An existing role with any elevated attribute is never converged (it could be an admin role).
privileged="$(NTS_DB_USER="$db_user" psql_admin -At <<'SQL'
\getenv db_user NTS_DB_USER
SELECT count(*) FROM pg_roles WHERE rolname = :'db_user'
  AND (rolsuper OR rolcreaterole OR rolreplication OR rolbypassrls);
SQL
)" || die 1 "cannot read pg_roles"
[ "$privileged" = "0" ] || die 1 "role '$db_user' already exists with SUPERUSER/CREATEROLE/REPLICATION/BYPASSRLS; choose another POSTGRES_USER"

# The password reaches psql only through its environment (\getenv), never argv.
NTS_DB_NAME="$db_name" NTS_DB_USER="$db_user" NTS_DB_PASSWORD="$db_password" psql_admin <<'SQL' || die 1 "creating or converging the role/database failed"
\getenv db_name NTS_DB_NAME
\getenv db_user NTS_DB_USER
\getenv db_password NTS_DB_PASSWORD
SELECT NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'db_user') AS create_role \gset
\if :create_role
  \echo 'creating role'
  CREATE ROLE :"db_user" LOGIN CREATEDB PASSWORD :'db_password';
\else
  ALTER ROLE :"db_user" LOGIN CREATEDB PASSWORD :'db_password';
\endif
SELECT NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db_name') AS create_db \gset
\if :create_db
  \echo 'creating database'
  CREATE DATABASE :"db_name" OWNER :"db_user";
\else
  SELECT pg_get_userbyid(datdba) <> :'db_user' AS fix_owner FROM pg_database WHERE datname = :'db_name' \gset
  \if :fix_owner
    \echo 'changing database owner'
    ALTER DATABASE :"db_name" OWNER TO :"db_user";
  \endif
\endif
SQL

log "ok: role '$db_user' and database '$db_name' on 127.0.0.1:5432"

if [ "$set_user_secrets" -eq 1 ]; then
  command -v dotnet >/dev/null 2>&1 || die 2 "dotnet not found (needed for --user-secrets)"
  for project in src/NightTales.Api src/NightTales.Worker; do
    # JSON is built from the parsed values and piped on stdin, so the password never reaches argv.
    NTS_DB_NAME="$db_name" NTS_DB_USER="$db_user" NTS_DB_PASSWORD="$db_password" python3 -c '
import json, os
cs = "Host=127.0.0.1;Port=5432;Database={};Username={};Password={}".format(
    os.environ["NTS_DB_NAME"], os.environ["NTS_DB_USER"], os.environ["NTS_DB_PASSWORD"])
print(json.dumps({"ConnectionStrings:Postgres": cs}))' \
      | dotnet user-secrets set --project "$repo_root/$project" >/dev/null \
      || die 1 "dotnet user-secrets set failed for $project"
  done
  log "ok: ConnectionStrings:Postgres stored in user-secrets for the Api and the Worker"
fi
