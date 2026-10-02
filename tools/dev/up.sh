#!/bin/bash
# tools/dev/up.sh - start the NightTales dev platform natively on macOS, without Docker
# (11.1.1-T1, D14, 11 CR-16). Idempotent: a service that is already running is left alone.
# Exit codes: 0 ok, 1 check failed, 2 usage / environment.
set -euo pipefail
umask 077

usage() {
  cat <<'EOF'
Usage: tools/dev/up.sh [--web] [--skip-storage-init] [--help]

Starts the native dev stack, every listener on 127.0.0.1 (no Docker):
  1. builds src/NightTales.Api, src/NightTales.Worker and tools/mock-mpt once, sequentially
     (OpenApiGenerateDocuments off, the Dockerfile publish flag); a failed build stops here
  2. the Homebrew postgresql@17 service, then tools/dev/db-init.sh (role and database, idempotent)
  3. seaweedfs (S3 8333, volume 8084) and mailpit (SMTP 1025, UI 8025) as up.sh-owned processes
  4. tools/dev/storage-init.sh when that script exists (11.1.1-T2 creates the dev buckets)
  5. starts each host with dotnet run --no-build: Api (5000), Worker (health 8082), mock-mpt (8090)
  6. with --web: ng serve (4200)
then waits for every health endpoint (DEV_HEALTH_TIMEOUT, default 90 s) and prints the URLs.

Flags:
  --web                 also start the web app (ng serve on 127.0.0.1:4200)
  --skip-storage-init   do not run tools/dev/storage-init.sh
  -h, --help            this text

Values come from .env (copy .env.example .env and set your own POSTGRES_PASSWORD); the port rows
are DEV_* and MOCK_PORT. Runtime state (pid files, logs, dev key material, emulator data) lives in
.nts/dev/, which is git-ignored. Stop the stack with tools/dev/down.sh.
EOF
}

web=0
skip_storage_init=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    --web) web=1 ;;
    --skip-storage-init) skip_storage_init=1 ;;
    *) echo "up.sh: unknown argument: $1 (see --help)" >&2; exit 2 ;;
  esac
  shift
done

log() { printf '%s up.sh: %s\n' "$(date -u +%FT%TZ)" "$*" >&2; }
warn() { log "WARNING: $*"; }
die() { local code="$1"; shift; log "ERROR: $*"; exit "$code"; }

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo_root"

# Shared helpers (service patterns, process identity, the Homebrew cluster check) live in lib.sh.
# shellcheck source=tools/dev/lib.sh
. "$repo_root/tools/dev/lib.sh"

env_file="$repo_root/.env"
example_file="$repo_root/.env.example"
nts_dev="${NTS_HOME:-$repo_root/.nts}/dev"
pid_dir="$nts_dev/pid"
log_dir="$nts_dev/log"
config_dir="$nts_dev/config"

# Reads KEY=VALUE from a dotenv file without sourcing it (no code execution), the same rules
# tools/dev/db-init.sh uses: quoted values carry no escapes, an unquoted " #" starts a comment.
# Returns 1 when the key is absent and 2 when the value is malformed.
env_value() {
  local file="$1" key="$2" line value
  [ -f "$file" ] || return 1
  line="$(grep -E "^[[:space:]]*${key}=" "$file" | tail -n 1)" || return 1
  value="${line#*=}"
  value="${value%$'\r'}"
  local dq='^"([^"]*)"[[:space:]]*(#.*)?$' sq="^'([^']*)'[[:space:]]*(#.*)?\$"
  case "$value" in
    \"*|\'*)
      if [[ "$value" =~ $dq ]] || [[ "$value" =~ $sq ]]; then
        value="${BASH_REMATCH[1]}"
      else
        return 2
      fi
      ;;
    *)
      value="${value%%[[:space:]]#*}"
      value="${value%"${value##*[![:space:]]}"}"
      ;;
  esac
  printf '%s' "$value"
}

# row KEY DEFAULT: the .env value, else the .env.example value, else the default. A malformed value
# in either file is a usage error (exit 2).
row() {
  local key="$1" fallback="$2" value rc=0
  value="$(env_value "$env_file" "$key")" || rc=$?
  [ "$rc" -ne 2 ] || die 2 "malformed value for $key in $env_file"
  if [ "$rc" -eq 0 ] && [ -n "$value" ]; then printf '%s' "$value"; return 0; fi
  rc=0
  value="$(env_value "$example_file" "$key")" || rc=$?
  [ "$rc" -ne 2 ] || die 2 "malformed value for $key in $example_file"
  if [ "$rc" -eq 0 ] && [ -n "$value" ]; then printf '%s' "$value"; return 0; fi
  printf '%s' "$fallback"
}

# owned_running NAME: prints the pid and returns 0 only when the pid file names a live process whose
# command line matches that service AND whose stored instance identity still matches (lib.sh's
# nts_pid_owned). Anything else - missing, dead, or a pid recycled by an unrelated process - is not
# ours, so up.sh never reports a foreign process as its own (nor down.sh kills one).
owned_running() {
  local pid
  pid="$(nts_pid_owned "$1" "$pid_dir/$1.pid" "$pid_dir/$1.identity")" || return 1
  printf '%s' "$pid"
}

# needs_start NAME: 0 when the caller must start the service, 1 when it is already running.
needs_start() {
  local name="$1" pid
  if pid="$(owned_running "$name")"; then
    log "$name already running (pid $pid), left alone"
    return 1
  fi
  rm -f "$pid_dir/$name.pid" "$pid_dir/$name.identity"
  return 0
}

# record_identity NAME PID: claim the just-started process by pid and instance identity, so a later
# run (or down.sh) can prove it is ours before touching it. A process that never reaches its final
# command line is not recorded.
record_identity() {
  nts_record_identity "$pid_dir/$1.pid" "$pid_dir/$1.identity" "$1" "$2" \
    || warn "could not record the identity of the $1 process (pid $2); it is left unclaimed"
}

# require_port NAME VALUE: every port row read from .env must be a plain integer in 1-65535. A
# delimiter or a URL smuggled into a port (e.g. DEV_API_PORT='5100;http://0.0.0.0:5101') would
# otherwise become a second ASP.NET Core listener and open the host to the LAN.
require_port() {
  local name="$1" value="$2"
  case "$value" in
    ''|*[!0-9]*|??????*) die 2 "$name must be an integer port 1-65535 (found: '$value')" ;;
  esac
  [ "$value" -ge 1 ] || die 2 "$name must be an integer port 1-65535 (found: $value)"
  [ "$value" -le 65535 ] || die 2 "$name must be an integer port 1-65535 (found: $value)"
}

port_in_use() { [ -n "$(lsof -nP -iTCP:"$1" -sTCP:LISTEN 2>/dev/null || true)" ]; }

check_port_free() {
  if port_in_use "$1"; then
    die 1 "port $1 ($2) is already in use: stop that process or set its row in .env"
  fi
}

http_ok() { curl -fsS -o /dev/null --max-time 5 "$1"; }

# http_ok_with_key URL KEY: the header goes through stdin, so the key never reaches argv (01 D9).
http_ok_with_key() { printf 'x-api-key: %s\n' "$2" | curl -fsS -o /dev/null --max-time 5 -H @- "$1"; }

# wait_until LABEL TIMEOUT CMD...: run CMD every second until it succeeds or the timeout expires.
wait_until() {
  local label="$1" timeout="$2" deadline
  shift 2
  deadline=$((SECONDS + timeout))
  while :; do
    if "$@"; then log "$label ready"; return 0; fi
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 1
  done
}

# ---------------------------------------------------------------- environment

[ -f "$env_file" ] || die 2 "$env_file is missing: cp .env.example .env, then set your own POSTGRES_PASSWORD"

db_pass="$(env_value "$env_file" POSTGRES_PASSWORD)" || true
[ -n "$db_pass" ] || die 2 "POSTGRES_PASSWORD is not set in .env (cp .env.example .env, then set your own)"

db_name="$(row POSTGRES_DB nighttales)"
db_user="$(row POSTGRES_USER nighttales)"
app_environment="$(row ASPNETCORE_ENVIRONMENT Development)"
app_public_origin="$(row APP__PUBLICORIGIN http://localhost:4200)"
dev_api_port="$(row DEV_API_PORT 5000)"
dev_worker_health_port="$(row DEV_WORKER_HEALTH_PORT 8082)"
dev_mock_port="$(row MOCK_PORT 8090)"
dev_web_port="$(row DEV_WEB_PORT 4200)"
dev_storage_port="$(row DEV_STORAGE_PORT 8333)"
dev_storage_volume_port="$(row DEV_STORAGE_VOLUME_PORT 8084)"
# SeaweedFS keeps its master status API on the upstream default (11.1.1-T2 pins the command line
# without a master flag), so the port is fixed here and only checked for conflicts.
storage_master_port=9333
dev_mail_smtp_port="$(row DEV_MAIL_SMTP_PORT 1025)"
dev_mail_ui_port="$(row DEV_MAIL_UI_PORT 8025)"
health_timeout="$(row DEV_HEALTH_TIMEOUT 90)"
dev_kek_pass="$(row DEV_KEK_PASSWORD change-me-local-dp-kek)"
mock_api_key="$(row MOCK_API_KEY dev-mock-api-key-local)"
mock_fixture="$(row MOCK_FIXTURE '')"
mock_render_seconds="$(row MOCK_RENDER_SECONDS 20)"
storage_access="$(row STORAGE_ACCESS_KEY dev)"
storage_secret="$(row STORAGE_SECRET_KEY dev-secret-local)"
# The flat STORAGE_*/SMTP_* rows 11.1.1-T2 documents in .env.example. The hosts read the storage rows
# under the canonical Storage:* property names of StorageSettings (ServiceUrl/AccessKey/SecretKey/
# Region/Buckets:{Media,Public,Music}); the mail rows must reach the canonical Email:Smtp:*/Email:From:
# Address keys, not the `Smtp` alias EmailOptions binds, because both hosts' appsettings.json already
# sets Email:Smtp:Host/Port and the alias loses to it (09.2.6-T3).
storage_endpoint="$(row STORAGE_ENDPOINT http://127.0.0.1:8333)"
storage_region="$(row STORAGE_REGION us-east-1)"
storage_bucket_media="$(row STORAGE_BUCKET_MEDIA nt-media-dev)"
storage_bucket_public="$(row STORAGE_BUCKET_PUBLIC nt-public-dev)"
storage_bucket_music="$(row STORAGE_BUCKET_MUSIC nt-music-dev)"
smtp_host="$(row SMTP_HOST 127.0.0.1)"
smtp_port="$(row SMTP_PORT 1025)"
smtp_user="$(row SMTP_USER dev)"
smtp_password="$(row SMTP_PASSWORD dev-secret-local)"
smtp_from="$(row SMTP_FROM dev@nighttales.local)"

case "$health_timeout" in ''|*[!0-9]*) die 2 "DEV_HEALTH_TIMEOUT must be a number of seconds" ;; esac

# Every port row is validated before anything is built or started (exit 2): a malformed value must
# never reach ASPNETCORE_URLS or an emulator's command line as a second listener.
require_port DEV_API_PORT "$dev_api_port"
require_port DEV_WORKER_HEALTH_PORT "$dev_worker_health_port"
require_port MOCK_PORT "$dev_mock_port"
require_port DEV_WEB_PORT "$dev_web_port"
require_port DEV_STORAGE_PORT "$dev_storage_port"
require_port DEV_STORAGE_VOLUME_PORT "$dev_storage_volume_port"
require_port DEV_MAIL_SMTP_PORT "$dev_mail_smtp_port"
require_port DEV_MAIL_UI_PORT "$dev_mail_ui_port"
require_port SMTP_PORT "$smtp_port"

# A value that must survive a dotenv round trip (native env and the compose env_file) may not carry
# whitespace, a double quote or a dollar sign.
case "$dev_kek_pass" in
  *[[:space:]\$\"]*) die 2 "DEV_KEK_PASSWORD must not contain whitespace, a double quote or a dollar sign" ;;
esac

connection_string="Host=127.0.0.1;Port=5432;Database=$db_name;Username=$db_user;Password=$db_pass"

# ---------------------------------------------------------------- preflight (nothing started yet)

command -v brew >/dev/null 2>&1 || die 1 "brew not found: install Homebrew (docs/dev-setup.md)"
command -v curl >/dev/null 2>&1 || die 1 "curl not found: install Xcode command line tools"
command -v lsof >/dev/null 2>&1 || die 1 "lsof not found: install Xcode command line tools"
command -v openssl >/dev/null 2>&1 || die 1 "openssl not found: brew install openssl"
command -v dotnet >/dev/null 2>&1 || die 1 "dotnet not found: install the .NET SDK 10.0.401 (docs/dev-setup.md)"
sdk_version="$(dotnet --version 2>/dev/null)" \
  || die 1 "the .NET SDK global.json pins is not installed: dotnet --version failed (docs/dev-setup.md)"
case "$sdk_version" in 10.*) ;; *) die 1 "the .NET SDK global.json pins is 10.x, found $sdk_version" ;; esac

pg_prefix="$(brew --prefix postgresql@17 2>/dev/null)" \
  || die 1 "postgresql@17 not installed: brew install postgresql@17"
pg_bin="$pg_prefix/bin"
[ -x "$pg_bin/pg_isready" ] || die 1 "postgresql@17 not installed: brew install postgresql@17"
pg_isready_ok() { "$pg_bin/pg_isready" -q -h 127.0.0.1 -p 5432; }

command -v weed >/dev/null 2>&1 || die 1 "weed not found: brew install seaweedfs"
command -v mailpit >/dev/null 2>&1 || die 1 "mailpit not found: brew install mailpit"

if [ "$web" -eq 1 ]; then
  [ -f "$repo_root/web/angular.json" ] \
    || die 1 "--web needs the web app, which is not in this tree yet (web/angular.json is missing; 10.1.1-T1)"
fi

mock_present=0
if [ -d "$repo_root/tools/mock-mpt" ]; then mock_present=1; fi

# 8081 is the Api's internal listener (02 D10). Its port is code-configured (02.5.2-T2), so it is
# checked here but has no .env row yet.
owned_running api >/dev/null || {
  check_port_free 8081 "Api internal listener (02.5.2-T2)"
  check_port_free "$dev_api_port" "Api (DEV_API_PORT)"
}
owned_running worker >/dev/null || check_port_free "$dev_worker_health_port" "Worker health (DEV_WORKER_HEALTH_PORT)"
owned_running seaweedfs >/dev/null || {
  check_port_free "$dev_storage_port" "SeaweedFS S3 (DEV_STORAGE_PORT)"
  check_port_free "$dev_storage_volume_port" "SeaweedFS volume (DEV_STORAGE_VOLUME_PORT)"
  check_port_free "$storage_master_port" "SeaweedFS master status (9333)"
}
owned_running mailpit >/dev/null || {
  check_port_free "$dev_mail_smtp_port" "Mailpit SMTP (DEV_MAIL_SMTP_PORT)"
  check_port_free "$dev_mail_ui_port" "Mailpit UI (DEV_MAIL_UI_PORT)"
}
if [ "$mock_present" -eq 1 ]; then
  owned_running mock >/dev/null || check_port_free "$dev_mock_port" "mock-mpt (MOCK_PORT)"
fi
if [ "$web" -eq 1 ]; then
  owned_running web >/dev/null || check_port_free "$dev_web_port" "web (DEV_WEB_PORT)"
fi

mkdir -p "$pid_dir" "$log_dir" "$config_dir" "$nts_dev/seaweedfs" "$nts_dev/mailpit"

# ---------------------------------------------------------------- application hosts: build once
# The .NET hosts share project outputs, so parallel `dotnet run`s race on them — a real MSB4018
# ("NightTales.Infrastructure.deps.json ... being used by another process"). Build each host that has
# to start, once and sequentially, before any host (and before the seaweedfs/mailpit emulators) starts;
# each host then runs with --no-build, and a failed build stops here with the log path, leaving no
# up.sh-owned process behind. The build passes -p:OpenApiGenerateDocuments=false, the same flag both
# Dockerfiles use on publish: the Api's build-time OpenAPI generator boots the host through
# dotnet-getdocument without this script's start-up env, which fails on a fresh clone. The document is
# generated by 09.1.5-T1's CI build instead — CI sets those values and never runs this script.
build_host() {
  local project="$1"
  log "building $project -> $log_dir/build.log"
  dotnet build "$repo_root/$project" --nologo -v q -p:OpenApiGenerateDocuments=false >>"$log_dir/build.log" 2>&1 \
    || die 1 "dotnet build $project failed (see $log_dir/build.log)"
}

owned_running api >/dev/null || build_host src/NightTales.Api
owned_running worker >/dev/null || build_host src/NightTales.Worker
if [ "$mock_present" -eq 1 ]; then
  owned_running mock >/dev/null || build_host tools/mock-mpt
fi

# ---------------------------------------------------------------- dev key material
# The Api validates App:PublicOrigin, the ES256 signing key and the DataProtection KEK at start-up,
# and the Worker shares the same ring (09.3.2-T1, 09.9.4-T1). Nothing is committed: throwaway values
# are generated once into .nts/dev/config (git-ignored) and reused, exactly as CI does per run.
jwt_pem="$config_dir/jwt-signing-key.pem"
kek_pfx="$config_dir/dp-kek.pfx"
kek_pass_file="$config_dir/dp-kek.password"
compose_env_file="$config_dir/compose.env"

if [ ! -s "$jwt_pem" ]; then
  log "generating a throwaway ES256 signing key in $jwt_pem"
  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$jwt_pem"
fi

kek_ok=0
if [ -s "$kek_pfx" ] && [ -s "$kek_pass_file" ] \
  && openssl pkcs12 -in "$kek_pfx" -passin "file:$kek_pass_file" -noout >/dev/null 2>&1; then
  kek_ok=1
fi
if [ "$kek_ok" -eq 0 ]; then
  log "generating a throwaway Data Protection KEK in $kek_pfx"
  printf '%s\n' "$dev_kek_pass" > "$kek_pass_file"
  chmod 600 "$kek_pass_file"
  tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/nts-kek.XXXXXX")"
  openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj '/CN=nts-dev-dp-kek' \
    -keyout "$tmp_dir/kek.key" -out "$tmp_dir/kek.crt" >/dev/null 2>&1
  openssl pkcs12 -export -out "$kek_pfx" -inkey "$tmp_dir/kek.key" -in "$tmp_dir/kek.crt" \
    -passout "file:$kek_pass_file" >/dev/null 2>&1
  rm -rf "$tmp_dir"
else
  # The PFX, its password file and DEV_KEK_PASSWORD (which the native hosts and the compose services
  # all use) are one secret. If .env was changed after the PFX was generated, the hosts would open it
  # with a password the PFX does not carry and fail inside DataProtectionKek.TryLoad, so refuse here
  # instead of starting a stack that cannot protect anything. The secret is never printed.
  if [ "$(cat "$kek_pass_file")" != "$dev_kek_pass" ]; then
    die 2 "DEV_KEK_PASSWORD in .env does not match the password stored with $kek_pfx; restore that row or delete $kek_pfx and $kek_pass_file to generate a new throwaway KEK (docs/dev-setup.md)"
  fi
fi
chmod 600 "$jwt_pem" "$kek_pfx" "$kek_pass_file"

# The optional compose api/worker read their signing key from this env file (a PEM cannot travel
# through `${...}` interpolation); the KEK password and path are plain compose values.
{
  printf 'Auth__Jwt__SigningKey="%s"\n' "$(cat "$jwt_pem")"
} > "$compose_env_file"
chmod 600 "$compose_env_file"

# ---------------------------------------------------------------- PostgreSQL

pg_state="$(brew services list | awk '$1 == "postgresql@17" { print $2 }')"
case "$pg_state" in
  started) log "postgresql@17 service already started" ;;
  '') die 1 "postgresql@17 is not managed by brew services: brew install postgresql@17" ;;
  *)
    log "starting the postgresql@17 service (was: $pg_state)"
    brew services start postgresql@17 >/dev/null || die 1 "brew services start postgresql@17 failed"
    ;;
esac
wait_until "PostgreSQL 127.0.0.1:5432" 30 pg_isready_ok \
  || die 1 "PostgreSQL did not accept connections on 127.0.0.1:5432 in 30 s; see: brew services list"

"$repo_root/tools/dev/db-init.sh" || { rc=$?; die "$rc" "tools/dev/db-init.sh failed (see above)"; }

# ---------------------------------------------------------------- emulators (loopback, up.sh-owned)
# Never `brew services` for these two: the stock definitions take no flags and bind every interface
# (11 CR-16). SeaweedFS keeps its volume server on 8084, off MPT's 8080 (01 D4).

start_seaweedfs() {
  needs_start seaweedfs || return 0
  log "starting seaweedfs (S3 127.0.0.1:$dev_storage_port) -> $log_dir/seaweedfs.log"
  (
    export AWS_ACCESS_KEY_ID="$storage_access"
    export AWS_SECRET_ACCESS_KEY="$storage_secret"
    exec weed server -ip=127.0.0.1 -ip.bind=127.0.0.1 -dir="$nts_dev/seaweedfs" \
      -volume.port="$dev_storage_volume_port" -master.volumeSizeLimitMB=64 \
      -s3 -s3.port="$dev_storage_port"
  ) >>"$log_dir/seaweedfs.log" 2>&1 &
  record_identity seaweedfs "$!"
}

start_mailpit() {
  needs_start mailpit || return 0
  log "starting mailpit (SMTP 127.0.0.1:$dev_mail_smtp_port, UI 127.0.0.1:$dev_mail_ui_port) -> $log_dir/mailpit.log"
  (
    exec mailpit -q --disable-version-check \
      --smtp "127.0.0.1:$dev_mail_smtp_port" \
      --listen "127.0.0.1:$dev_mail_ui_port" \
      --database "$nts_dev/mailpit/mailpit.db" \
      --smtp-auth-accept-any --smtp-auth-allow-insecure
  ) >>"$log_dir/mailpit.log" 2>&1 &
  record_identity mailpit "$!"
}

seaweedfs_ok() { http_ok "http://127.0.0.1:$storage_master_port/cluster/status"; }
mailpit_ok() { http_ok "http://127.0.0.1:$dev_mail_ui_port/api/v1/info"; }

start_seaweedfs
start_mailpit

wait_until "SeaweedFS master 127.0.0.1:$storage_master_port" 30 seaweedfs_ok \
  || die 1 "SeaweedFS did not answer http://127.0.0.1:$storage_master_port/cluster/status in 30 s; see $log_dir/seaweedfs.log"
wait_until "Mailpit 127.0.0.1:$dev_mail_ui_port" 30 mailpit_ok \
  || die 1 "Mailpit did not answer http://127.0.0.1:$dev_mail_ui_port/api/v1/info in 30 s; see $log_dir/mailpit.log"

# ---------------------------------------------------------------- dev buckets (11.1.1-T2)

storage_init="$repo_root/tools/dev/storage-init.sh"
if [ "$skip_storage_init" -eq 1 ]; then
  log "--skip-storage-init: not running tools/dev/storage-init.sh"
elif [ -x "$storage_init" ]; then
  log "running tools/dev/storage-init.sh"
  "$storage_init" || { rc=$?; die "$rc" "tools/dev/storage-init.sh failed (see above)"; }
else
  warn "tools/dev/storage-init.sh is not present yet (11.1.1-T2): the dev buckets are not created"
fi

# ---------------------------------------------------------------- application hosts: start
# Each *_environment function runs inside the background subshell of its start function, so the
# values stay out of every argv (01 D9) and reach the child through the environment.

# storage_mail_environment: the canonical Storage:* property keys and the canonical Email__Smtp__*/
# Email__From__Address keys both hosts read (11.1.1-T2, T41; 09.7.2-T1; 09.2.6-T3). The flat .env names
# (STORAGE_*, SMTP_*) are shell input only, never .NET configuration: the storage rows must be exported
# under the StorageSettings property names — `Storage__ACCESS_KEY`/`STORAGE__BUCKET_MEDIA` would bind as
# `Storage:Access_Key`/`Storage:BucketMedia`, which match no property (only a `Storage:Endpoint` alias
# for ServiceUrl exists, in AddStorage). The mail rows likewise reach Email:Smtp:*: EmailOptions binds
# the `Smtp` alias *before* Email:Smtp:* and both hosts' appsettings.json already commits
# Email:Smtp:Host/Port, so a flat SMTP__HOST/SMTP__PORT loses to those committed values (and SMTP__FROM
# maps to no property at all). The emulators listen on loopback, so these are the loopback values; the
# compose variant overrides the host part to the service names (`storage:8333`, `mail:1025`) in
# docker-compose.yml. Credentials travel in the environment, never in argv (01 D9) — this runs inside the
# host's subshell.
storage_mail_environment() {
  export Storage__ServiceUrl="$storage_endpoint"
  export Storage__AccessKey="$storage_access"
  export Storage__SecretKey="$storage_secret"
  export Storage__Region="$storage_region"
  export Storage__Buckets__Media="$storage_bucket_media"
  export Storage__Buckets__Public="$storage_bucket_public"
  export Storage__Buckets__Music="$storage_bucket_music"
  export Email__Smtp__Host="$smtp_host"
  export Email__Smtp__Port="$smtp_port"
  export Email__Smtp__Username="$smtp_user"
  export Email__Smtp__Password="$smtp_password"
  export Email__From__Address="$smtp_from"
}

api_environment() {
  local signing_key
  signing_key="$(cat "$jwt_pem")"
  export ASPNETCORE_ENVIRONMENT="$app_environment"
  export ASPNETCORE_URLS="http://127.0.0.1:$dev_api_port"
  export App__PublicOrigin="$app_public_origin"
  export ConnectionStrings__Postgres="$connection_string"
  export Auth__Jwt__SigningKey="$signing_key"
  export DataProtection__KekPfxPath="$kek_pfx"
  export DataProtection__KekPfxPassword="$dev_kek_pass"
  storage_mail_environment
  # The 8081 internal listener is code-configured by 02.5.2-T2; the row is set here so the address
  # never moves off loopback (T10).
  export Fetcher__Internal__ListenAddress="127.0.0.1"
}

worker_environment() {
  export ASPNETCORE_ENVIRONMENT="$app_environment"
  export Worker__HealthPort="$dev_worker_health_port"
  # The Worker's health listener binds Worker:HealthAddress (default 127.0.0.1). Pinned here so the
  # native address never moves off loopback; the optional compose service overrides it to 0.0.0.0
  # because a published port reaches the container's interface, not its loopback.
  export Worker__HealthAddress="127.0.0.1"
  export ConnectionStrings__Postgres="$connection_string"
  export DataProtection__KekPfxPath="$kek_pfx"
  export DataProtection__KekPfxPassword="$dev_kek_pass"
  storage_mail_environment
  if [ "$mock_present" -eq 1 ]; then
    # D13: the node tuple the Worker's registry binds; the key is RENDER_NODE_<NAME>_API_KEY (F26).
    export RENDER_NODE_MOCK_API_KEY="${mock_api_key}"
    export RenderNodes__Nodes__0__Name="mock"
    export RenderNodes__Nodes__0__BaseUrl="http://127.0.0.1:$dev_mock_port"
    export RenderNodes__Nodes__0__Capacity="1"
  fi
}

mock_environment() {
  export ASPNETCORE_ENVIRONMENT="$app_environment"
  export ASPNETCORE_URLS="http://127.0.0.1:$dev_mock_port"
  export MOCK_API_KEY="${mock_api_key}"
  export MOCK_FIXTURE="$mock_fixture"
  export MOCK_RENDER_SECONDS="$mock_render_seconds"
}

start_api() {
  needs_start api || return 0
  log "starting Api (127.0.0.1:$dev_api_port) -> $log_dir/api.log"
  (
    api_environment
    exec dotnet run --project src/NightTales.Api --no-launch-profile --no-build
  ) >>"$log_dir/api.log" 2>&1 &
  record_identity api "$!"
}

start_worker() {
  needs_start worker || return 0
  log "starting Worker (health 127.0.0.1:$dev_worker_health_port) -> $log_dir/worker.log"
  (
    worker_environment
    exec dotnet run --project src/NightTales.Worker --no-launch-profile --no-build
  ) >>"$log_dir/worker.log" 2>&1 &
  record_identity worker "$!"
}

start_mock() {
  if [ "$mock_present" -eq 0 ]; then
    warn "tools/mock-mpt is not in this tree yet (11.1.2-T1): the mock render node and its health check are skipped"
    return 0
  fi
  needs_start mock || return 0
  log "starting mock-mpt (127.0.0.1:$dev_mock_port) -> $log_dir/mock.log"
  (
    mock_environment
    exec dotnet run --project tools/mock-mpt --no-launch-profile --no-build
  ) >>"$log_dir/mock.log" 2>&1 &
  record_identity mock "$!"
}

start_web() {
  [ "$web" -eq 1 ] || return 0
  needs_start web || return 0
  log "starting the web app (ng serve 127.0.0.1:$dev_web_port) -> $log_dir/web.log"
  (
    cd "$repo_root/web"
    exec npx ng serve --host 127.0.0.1 --port "$dev_web_port"
  ) >>"$log_dir/web.log" 2>&1 &
  record_identity web "$!"
}

# ---------------------------------------------------------------- start the hosts
# Each host was built once above; --no-build keeps the shared project outputs untouched while they run.

start_api
start_worker
start_mock
start_web

# ---------------------------------------------------------------- health loop

service_health_url() {
  case "$1" in
    api) printf 'http://127.0.0.1:%s/health/live' "$dev_api_port" ;;
    worker) printf 'http://127.0.0.1:%s/health/ready' "$dev_worker_health_port" ;;
    mock) printf 'http://127.0.0.1:%s/v1/health' "$dev_mock_port" ;;
  esac
}

check_service_health() {
  if [ "$1" = mock ]; then
    http_ok_with_key "$(service_health_url mock)" "$mock_api_key"
  else
    http_ok "$(service_health_url "$1")"
  fi
}

remove_word() {
  local out="" word
  for word in $1; do [ "$word" = "$2" ] || out="$out $word"; done
  printf '%s' "${out# }"
}

pending="api worker"
if [ "$mock_present" -eq 1 ]; then pending="$pending mock"; fi
failed=""
health_deadline=$((SECONDS + health_timeout))
while [ -n "$pending" ] && [ "$SECONDS" -lt "$health_deadline" ]; do
  for service in $pending; do
    if check_service_health "$service"; then
      log "$service is healthy"
      pending="$(remove_word "$pending" "$service")"
    elif ! owned_running "$service" >/dev/null; then
      # A host whose process is gone cannot become healthy: report it now instead of waiting out
      # the whole timeout (a failed build is the usual cause).
      warn "$service is not running any more - read $log_dir/$service.log"
      failed="$failed $service"
      pending="$(remove_word "$pending" "$service")"
    fi
  done
  [ -z "$pending" ] || sleep 2
done

if [ -n "$pending" ]; then
  for service in $pending; do
    warn "$service is unhealthy after ${health_timeout}s ($(service_health_url "$service")) - read $log_dir/$service.log"
  done
fi
if [ -n "$pending$failed" ]; then
  die 1 "the stack is not healthy:$pending$failed - read the log files under $log_dir"
fi

# ---------------------------------------------------------------- service table

printf '\n'
printf 'Service        URL\n'
printf 'Api            http://127.0.0.1:%s\n' "$dev_api_port"
printf 'Worker health  http://127.0.0.1:%s/health/ready\n' "$dev_worker_health_port"
if [ "$mock_present" -eq 1 ]; then
  printf 'mock-mpt       http://127.0.0.1:%s\n' "$dev_mock_port"
else
  printf 'mock-mpt       not in this tree yet (11.1.2-T1)\n'
fi
printf 'Mailpit UI     http://127.0.0.1:%s\n' "$dev_mail_ui_port"
printf 'Storage (S3)   http://127.0.0.1:%s\n' "$dev_storage_port"
printf 'PostgreSQL     postgresql://%s@127.0.0.1:5432/%s\n' "$db_user" "$db_name"
if [ "$web" -eq 1 ]; then printf 'Web (ng serve) http://127.0.0.1:%s\n' "$dev_web_port"; fi
printf '\nLogs: %s/*.log   Stop: tools/dev/down.sh\n' "$log_dir"
