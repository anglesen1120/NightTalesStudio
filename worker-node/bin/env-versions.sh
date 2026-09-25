#!/usr/bin/env bash
# worker-node/bin/env-versions.sh — one JSON object with machine + tool versions (01.1.1-T1).
# Stored by trial/tools/start_run.sh as run.json key "env". Never prints paths, user, host or secrets.
set -euo pipefail

if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
  cat <<'EOF'
Usage: worker-node/bin/env-versions.sh
Prints one JSON object (16 keys, schema_version 1) on stdout; diagnostics on stderr.
Exit codes: 0 ok · 2 git, uv or ffmpeg missing (D9).
EOF
  exit 0
fi
[ $# -eq 0 ] || { echo "env-versions.sh: unexpected argument (see --help)" >&2; exit 2; }

# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

for t in git uv ffmpeg; do
  command -v "$t" >/dev/null 2>&1 || die 2 "required tool missing: $t"
done

NTS_HOME="$(nts_home 2>/dev/null)"

first_line() { sed -n 1p; }

E_CAPTURED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
E_MACOS_VERSION="$(sw_vers -productVersion)"
E_MACOS_BUILD="$(sw_vers -buildVersion)"
E_HW_MODEL="$(sysctl -n hw.model)"
E_CPU_BRAND="$(sysctl -n machdep.cpu.brand_string)"
E_MEMORY_BYTES="$(sysctl -n hw.memsize)"
E_GIT="$(git --version | awk '{print $3}')"
E_UV="$(uv --version | awk '{print $2}')"
E_FFMPEG="$(ffmpeg -version | first_line | awk '{print $3}')"
if command -v gitleaks >/dev/null 2>&1; then
  E_GITLEAKS="$(gitleaks version | first_line | sed 's/^v//')"
else
  E_GITLEAKS="n/a:not-installed"
fi
E_FFMPEG_ENCODERS="$(ffmpeg -hide_banner -encoders 2>/dev/null | awk '$2=="libmp3lame"||$2=="aac"{print $2}' | sort -u | tr '\n' ' ')"
# .nts sits inside this repo (CR-11): require the clone's own .git, or git would report our HEAD.
clone_head() { if [ -e "$1/.git" ]; then git -C "$1" rev-parse HEAD 2>/dev/null || echo 'n/a:not-cloned'; else echo 'n/a:not-cloned'; fi; }
E_UV_PYTHONS="$(uv python list --only-installed 2>/dev/null | grep -oE '3\.[0-9]+\.[0-9]+' | sort -u -t. -k1,1n -k2,2n -k3,3n | tr '\n' ' ' || true)"
E_MPT_COMMIT="$(clone_head "$NTS_HOME/src/mpt")"
E_VIENEU_COMMIT="$(clone_head "$NTS_HOME/src/vieneu")"
E_INTERNAL=false
p="$NTS_HOME"
while [ ! -e "$p" ] && [ "$p" != "/" ]; do p="$(dirname "$p")"; done
dev="$(df -P "$p" | awk 'NR==2{print $1}')"
if info="$(diskutil info "$dev" 2>/dev/null)" \
  && grep -Eq '^ *Device Location: +Internal' <<<"$info" \
  && grep -Eq '^ *FileVault: +Yes' <<<"$info"; then
  E_INTERNAL=true
fi

export E_CAPTURED_AT E_MACOS_VERSION E_MACOS_BUILD E_HW_MODEL E_CPU_BRAND E_MEMORY_BYTES \
  E_GIT E_UV E_FFMPEG E_GITLEAKS E_FFMPEG_ENCODERS E_UV_PYTHONS E_MPT_COMMIT E_VIENEU_COMMIT E_INTERNAL

uv run --no-project --python 3.12 python -c '
import json, os
e = os.environ
print(json.dumps({
    "schema_version": 1,
    "captured_at": e["E_CAPTURED_AT"],
    "macos_version": e["E_MACOS_VERSION"],
    "macos_build": e["E_MACOS_BUILD"],
    "hw_model": e["E_HW_MODEL"],
    "cpu_brand": e["E_CPU_BRAND"],
    "memory_bytes": int(e["E_MEMORY_BYTES"]),
    "git": e["E_GIT"],
    "uv": e["E_UV"],
    "ffmpeg": e["E_FFMPEG"],
    "gitleaks": e["E_GITLEAKS"],
    "ffmpeg_encoders": sorted(e["E_FFMPEG_ENCODERS"].split()),
    "uv_pythons": e["E_UV_PYTHONS"].split(),
    "mpt_commit": e["E_MPT_COMMIT"],
    "vieneu_commit": e["E_VIENEU_COMMIT"],
    "nts_home_on_internal_disk": e["E_INTERNAL"] == "true",
}, ensure_ascii=False))
'
