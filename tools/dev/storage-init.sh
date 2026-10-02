#!/bin/bash
# tools/dev/storage-init.sh - create the dev S3 buckets and the D15 prefix markers on the local
# SeaweedFS gateway (11.1.1-T2, D14/D15). Idempotent: an existing bucket is success, a re-run
# changes nothing, and the script never deletes a bucket or an object.
# Exit codes: 0 ok, 1 check failed, 2 usage / environment.
set -euo pipefail
umask 077

usage() {
  cat <<'EOF'
Usage: tools/dev/storage-init.sh [--help]

Creates the three dev S3 buckets (nt-media-dev, nt-public-dev, nt-music-dev) and the
D15 prefix markers (music/, music-evidence/) inside the music bucket on the local
SeaweedFS gateway. Safe to re-run; it never deletes.

Values come from the environment, else .env. The two credentials are read only from
the environment or .env (never .env.example) and never appear on any command line.

  STORAGE_ENDPOINT       S3 endpoint                   (default http://127.0.0.1:8333)
  STORAGE_ACCESS_KEY     access key id                 (required)
  STORAGE_SECRET_KEY     secret access key             (required)
  STORAGE_REGION         signing region                (default us-east-1)
  STORAGE_BUCKET_MEDIA   media bucket                  (default nt-media-dev)
  STORAGE_BUCKET_PUBLIC  public bucket                 (default nt-public-dev)
  STORAGE_BUCKET_MUSIC   music bucket                  (default nt-music-dev)
  STORAGE_WAIT_SECONDS   seconds to wait for SeaweedFS (default 60)

The S3 client is, in order: the host `aws` CLI (Homebrew awscli), `uvx --from awscli aws`,
else the pinned amazon/aws-cli container (11 CR-16, T11). Start SeaweedFS first with
tools/dev/up.sh (native) or `docker compose up -d storage` (compose variant).
EOF
}

case "${1:-}" in
  -h|--help) usage; exit 0 ;;
  "") ;;
  *) echo "storage-init.sh: unknown argument: $1 (see --help)" >&2; exit 2 ;;
esac
[ "$#" -le 1 ] || { echo "storage-init.sh: too many arguments (see --help)" >&2; exit 2; }

log() { printf '%s storage-init.sh: %s\n' "$(date -u +%FT%TZ)" "$*" >&2; }
die() { local code="$1"; shift; log "ERROR: $*"; exit "$code"; }

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"

# Reads KEY=VALUE from a dotenv file without sourcing it (no code execution), the same rules
# tools/dev/db-init.sh and up.sh use. Returns 1 when the key is absent, 2 when malformed.
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

# setting KEY DEFAULT FILE...: the environment first (the documented precedence for this script),
# else the first non-empty value from the files, else DEFAULT. A malformed line is a usage error.
setting() {
  local key="$1" fallback="$2" f v rc
  shift 2
  if [ -n "${!key:-}" ]; then printf '%s' "${!key}"; return 0; fi
  for f in "$@"; do
    rc=0
    v="$(env_value "$f" "$key")" || rc=$?
    [ "$rc" -ne 2 ] || die 2 "malformed value for $key in $f"
    if [ "$rc" -eq 0 ] && [ -n "$v" ]; then printf '%s' "$v"; return 0; fi
  done
  printf '%s' "$fallback"
}

# required_secret KEY: the value from the environment, else .env. An empty value counts as
# missing. .env.example is deliberately not consulted: its placeholder must never reach a
# live endpoint, and "STORAGE_SECRET_KEY unset" has to fail loudly.
required_secret() {
  local key="$1" value
  if [ -n "${!key:-}" ]; then printf '%s' "${!key}"; return 0; fi
  value="$(env_value "$env_file" "$key")" || true
  [ -n "$value" ] || return 1
  printf '%s' "$value"
}

env_file="$repo_root/.env"
example_file="$repo_root/.env.example"

endpoint="$(setting STORAGE_ENDPOINT "http://127.0.0.1:8333" "$env_file" "$example_file")"
region="$(setting STORAGE_REGION "us-east-1" "$env_file" "$example_file")"
bucket_media="$(setting STORAGE_BUCKET_MEDIA "nt-media-dev" "$env_file" "$example_file")"
bucket_public="$(setting STORAGE_BUCKET_PUBLIC "nt-public-dev" "$env_file" "$example_file")"
bucket_music="$(setting STORAGE_BUCKET_MUSIC "nt-music-dev" "$env_file" "$example_file")"
wait_seconds="$(setting STORAGE_WAIT_SECONDS "60" "$env_file" "$example_file")"

# Both credentials are required before anything is contacted, so a missing one never leaves a
# half-initialised endpoint. The message names the variable only, never a value.
access_key="$(required_secret STORAGE_ACCESS_KEY)" \
  || die 2 "STORAGE_ACCESS_KEY is not set (environment or .env; see .env.example)"
secret_key="$(required_secret STORAGE_SECRET_KEY)" \
  || die 2 "STORAGE_SECRET_KEY is not set (environment or .env; see .env.example)"

case "$endpoint" in
  http://*|https://*) ;;
  *) die 2 "STORAGE_ENDPOINT must start with http:// or https:// (found a value without a scheme)" ;;
esac
case "$region" in
  ''|*[!abcdefghijklmnopqrstuvwxyz0123456789-]*) die 2 "STORAGE_REGION must be a lowercase region name such as us-east-1" ;;
esac
case "$wait_seconds" in
  ''|*[!0-9]*) die 2 "STORAGE_WAIT_SECONDS must be a number of seconds" ;;
esac
for bucket in "$bucket_media" "$bucket_public" "$bucket_music"; do
  [[ "$bucket" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] \
    || die 2 "bucket name '$bucket' is not a valid S3 bucket name (lowercase letters, digits, '.' and '-')"
done
[ "$bucket_media" != "$bucket_public" ] && [ "$bucket_media" != "$bucket_music" ] \
  && [ "$bucket_public" != "$bucket_music" ] \
  || die 2 "STORAGE_BUCKET_MEDIA, STORAGE_BUCKET_PUBLIC and STORAGE_BUCKET_MUSIC must differ"

command -v curl >/dev/null 2>&1 || die 1 "curl not found: install the Xcode command line tools"

# The S3 client ladder (11 CR-16, T11): host aws CLI first, then the uvx form 04.2.3-T1 records,
# then the pinned container. Credentials always travel in the environment, never in argv.
aws_cli_image="amazon/aws-cli@sha256:e14216fb361cce909ce199616711ad103182d5937f851cda9bebf25867d7180a"
client=""
if command -v aws >/dev/null 2>&1; then
  client=aws
elif command -v uvx >/dev/null 2>&1; then
  client=uvx
elif command -v docker >/dev/null 2>&1; then
  client=docker
else
  die 2 "no S3 client found: brew install awscli (or install uv for 'uvx --from awscli')"
fi

export AWS_ACCESS_KEY_ID="$access_key"
export AWS_SECRET_ACCESS_KEY="$secret_key"
export AWS_DEFAULT_REGION="$region"
export AWS_EC2_METADATA_DISABLED=true
export AWS_PAGER=""

docker_env_file=""
docker_endpoint="$endpoint"
if [ "$client" = docker ]; then
  # The container reads the credentials from an env file (never argv). It is a throwaway 0600
  # file, not the repository .env: that file carries POSTGRES_PASSWORD and the STORAGE_* names,
  # not the AWS_* names the aws CLI reads.
  docker_env_file="$(mktemp "${TMPDIR:-/tmp}/nts-storage-init.XXXXXX")"
  trap 'rm -f "$docker_env_file"' EXIT
  printf 'AWS_ACCESS_KEY_ID=%s\nAWS_SECRET_ACCESS_KEY=%s\nAWS_DEFAULT_REGION=%s\n' \
    "$access_key" "$secret_key" "$region" >"$docker_env_file"
  # A loopback endpoint (`http://127.0.0.1:8333`, the native default, or localhost) is the helper
  # container's own namespace, not the host running SeaweedFS nor the compose publish, so the
  # container can never reach it. macOS Docker and colima resolve host.docker.internal to the host,
  # so only the container client dials that spelling; the host clients (aws, uvx) and the readiness
  # probe above keep the original endpoint. A non-loopback endpoint is passed through unchanged.
  case "$docker_endpoint" in
    http://127.0.0.1:*) docker_endpoint="http://host.docker.internal:${docker_endpoint#http://127.0.0.1:}" ;;
    http://localhost:*) docker_endpoint="http://host.docker.internal:${docker_endpoint#http://localhost:}" ;;
  esac
fi
log "S3 client: $client, endpoint $docker_endpoint, region $region"

# s3 ARGS...: run the resolved client against the configured endpoint. The endpoint and the
# operation are the only things in argv; no credential is.
s3() {
  case "$client" in
    aws) aws --endpoint-url "$endpoint" --region "$region" "$@" ;;
    uvx) uvx --from awscli aws --endpoint-url "$endpoint" --region "$region" "$@" ;;
    docker) docker run --rm --env-file "$docker_env_file" "$aws_cli_image" \
      --endpoint-url "$docker_endpoint" --region "$region" "$@" ;;
  esac
}

# storage_reachable: the native master status API when it is up (the probe tools/dev/up.sh uses),
# else any HTTP answer from the S3 endpoint root. The compose variant publishes only the S3 port;
# SeaweedFS answers 403 on "/" by design, and curl without -f counts that as a response.
master_status_url="http://127.0.0.1:9333/cluster/status"
storage_reachable() {
  curl -fsS -o /dev/null --max-time 3 "$master_status_url" 2>/dev/null && return 0
  curl -sS -o /dev/null --max-time 3 "${endpoint%/}/" 2>/dev/null
}

deadline=$((SECONDS + wait_seconds))
until storage_reachable; do
  [ "$SECONDS" -lt "$deadline" ] \
    || die 1 "SeaweedFS is not answering on $endpoint after ${wait_seconds}s: start it with tools/dev/up.sh (or 'docker compose up -d storage')"
  sleep 1
done
log "SeaweedFS is up on $endpoint"

bucket_exists() { s3 s3api head-bucket --bucket "$1" >/dev/null 2>&1; }

# create_bucket NAME: create it once, or accept the error an existing bucket produces (a bucket a
# different run created between the check and the call). Anything else exits 1 with the S3 output.
create_bucket() {
  local bucket="$1" err rc=0
  if bucket_exists "$bucket"; then log "bucket $bucket already exists"; return 0; fi
  err="$(s3 s3api create-bucket --bucket "$bucket" 2>&1)" || rc=$?
  if [ "$rc" -eq 0 ]; then log "created bucket $bucket"; return 0; fi
  case "$err" in
    *BucketAlreadyOwnedByYou*|*BucketAlreadyExists*)
      log "bucket $bucket already exists"; return 0 ;;
  esac
  if bucket_exists "$bucket"; then log "bucket $bucket created by another run"; return 0; fi
  die 1 "create-bucket $bucket failed: $err"
}

create_bucket "$bucket_media"
create_bucket "$bucket_public"
create_bucket "$bucket_music"

# D15 prefixes as zero-byte marker objects, so a fresh music bucket shows music/ and
# music-evidence/ before the first upload. put-object with no --body writes the zero-byte object
# (awscli v1 rejects a character device like /dev/null as a blob value); re-putting changes nothing.
put_marker() {
  s3 s3api put-object --bucket "$bucket_music" --key "$1" >/dev/null \
    || die 1 "put-object $bucket_music/$1 failed"
  log "prefix marker $bucket_music/$1 ready"
}
put_marker "music/"
put_marker "music-evidence/"

# Verify with the endpoint itself; any bucket the ListBuckets/HeadBucket path does not confirm is
# a failure named by bucket.
for bucket in "$bucket_media" "$bucket_public" "$bucket_music"; do
  bucket_exists "$bucket" || die 1 "head-bucket $bucket failed: the bucket is not readable after creation"
done

log "ready: $bucket_media, $bucket_public, $bucket_music (prefixes in $bucket_music)"
