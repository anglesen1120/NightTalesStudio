#!/usr/bin/env bash
# openai-access-check.sh — direct access tests for the two OpenAI(-compatible) keys (01.2.4-T3).
# Calls go to the pinned OPENAI_BASE_URL (pins.env, 01 DEC-9/CR-15), never through the usage tap.
# Denial checks C2/C4/C6/C7 are recorded but not failed: the relay scopes per group, not per
# key/model (owner, 01 DEC-10). C3 is one paid call for gpt-image-2 (01 DEC-11): with
# OPENAI_IMAGE_API=responses it goes to /responses with the image_generation tool on
# OPENAI_IMAGE_HOST_MODEL (the relay's /images/generations is broken), else to /images/generations.
# Every chat call sends reasoning_effort = OPENAI_EFFORT_CHAT (pins.env, 01 DEC-11).
set -euo pipefail
case "$-" in *x*) echo "openai-access-check.sh: refusing to run with xtrace (keys would be traced)" >&2; exit 2 ;; esac

# shellcheck source=worker-node/bin/lib.sh
. "$(dirname "$0")/lib.sh"

STORY_MODEL="gpt-6-luna"
IMAGE_MODEL="gpt-image-2"
DENIED_STORY_MODEL="gpt-6-astra"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/openai-access-check.sh [--only story|image|all] [--no-image-gen] [--help]
  --only story     run C0s, C1, C2, C6, C7
  --only image     run C0i, C3, C4
  --no-image-gen   skip C3 (the only paid image call)
Exit: 0 all executed checks pass · 1 a check failed · 2 usage/environment error.
Keys are read from the Keychain (nts.openai.story, nts.openai.image) and passed to curl only as a
header file from a process substitution; they never appear in argv, stdout or files.
EOF
}

ONLY=all NO_IMG=0
while [ $# -gt 0 ]; do
  case "$1" in
    --only) [ $# -ge 2 ] || { usage >&2; exit 2; }; shift
            case "$1" in story|image|all) ONLY="$1" ;; *) usage >&2; exit 2 ;; esac ;;
    --no-image-gen) NO_IMG=1 ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
  shift
done

command -v curl >/dev/null || die 2 "curl not found"
command -v uv >/dev/null || die 2 "uv not found"
NTS_HOME="$(nts_home)"
load_pins
umask 077
OUT="$NTS_HOME/runs/access-check-$(date -u +%Y%m%d-%H%M%S)"
mkdir -p "$OUT"

# api <service> <id> <method> <path> [<json-body-file>]
api() {
  local svc=$1 id=$2 method=$3 path=$4 body=${5:-} key code
  key=$(secret "$svc")
  local args=(-sS --max-time 180 -o "$OUT/$id.body" -w '%{http_code}' -X "$method"
              -H 'Content-Type: application/json')
  [[ -n $body ]] && args+=(--data-binary "@$body")
  code=$(curl "${args[@]}" -H @<(printf 'Authorization: Bearer %s\n' "$key") \
         "$OPENAI_BASE_URL$path" 2>/dev/null) || code=000
  unset key
  printf '%s' "$code" >"$OUT/$id.status"
}

# call <id> <svc> <method> <path> [body] — one retry after 10 s on 5xx/000.
call() {
  local id=$1
  api "$2" "$id" "$3" "$4" "${5:-}"
  case "$(cat "$OUT/$id.status")" in
    5*|000) sleep 10; api "$2" "$id" "$3" "$4" "${5:-}" ;;
  esac
}

IMG_PROMPT="A foggy empty village road at dusk, illustration"
chat_body() { printf '{"model":"%s","messages":[{"role":"user","content":"Reply with OK."}],"max_completion_tokens":16,"reasoning_effort":"%s"}' "$1" "$OPENAI_EFFORT_CHAT"; }
img_body() { printf '{"model":"%s","prompt":"%s","n":1,"size":"1024x1024","quality":"low"}' "$1" "$IMG_PROMPT"; }
# Same shape the usage tap sends when it translates images/generations (01 DEC-11).
img_resp_body() {
  printf '{"model":"%s","input":"%s","reasoning":{"effort":"%s"},"tools":[{"type":"image_generation","model":"%s","size":"1024x1024","quality":"low"}],"tool_choice":{"type":"image_generation"}}' \
    "$OPENAI_IMAGE_HOST_MODEL" "$IMG_PROMPT" "$OPENAI_EFFORT_IMAGE_HOST" "$1"
}

chat_body "$STORY_MODEL" >"$OUT/chat.req.json"
chat_body "$DENIED_STORY_MODEL" >"$OUT/C7.req.json"
img_body "$IMAGE_MODEL" >"$OUT/img.req.json"
IDS=()

if [ "$ONLY" != image ]; then
  call C0s nts.openai.story GET /models; IDS+=(C0s)
  call C1 nts.openai.story POST /chat/completions "$OUT/chat.req.json"; IDS+=(C1)
  call C2 nts.openai.story POST /images/generations "$OUT/img.req.json"; IDS+=(C2)
  call C6 nts.openai.story GET /files; IDS+=(C6)
  call C7 nts.openai.story POST /chat/completions "$OUT/C7.req.json"; IDS+=(C7)
fi
if [ "$ONLY" != story ]; then
  call C0i nts.openai.image GET /models; IDS+=(C0i)
  if [ "$NO_IMG" -eq 0 ]; then
    if [ "$OPENAI_IMAGE_API" = responses ]; then
      img_resp_body "$IMAGE_MODEL" >"$OUT/C3.req.json"
      api nts.openai.image C3 POST /responses "$OUT/C3.req.json"            # paid: no automatic retry
    else
      api nts.openai.image C3 POST /images/generations "$OUT/img.req.json"  # paid: no automatic retry
    fi
    IDS+=(C3)
  fi
  call C4 nts.openai.image POST /chat/completions "$OUT/chat.req.json"; IDS+=(C4)
fi

REC="$NTS_REPO_ROOT/trial/baselines/openai-access-mac-$(date -u +%Y%m%d).json"
uv run --no-project --python 3.12 python - "$OUT" "$REC" "$OPENAI_BASE_URL" "$IMAGE_MODEL" "$OPENAI_IMAGE_API" "$OPENAI_EFFORT_CHAT" "$OPENAI_EFFORT_IMAGE_HOST" "${IDS[@]}" <<'PY'
import base64, json, os, sys
out, rec, base, c3_model, image_api, effort_chat, effort_img = sys.argv[1:8]
ids = sys.argv[8:]
DENY = {"C2", "C4", "C6", "C7"}   # recorded only: relay scopes per group, not per key/model (01 DEC-10)
LABEL = {"C0s": "story models", "C0i": "image models", "C1": "story chat", "C2": "story images",
         "C3": "image images", "C4": "image chat", "C6": "story files", "C7": "story astra"}

def load(i):
    st = open(os.path.join(out, i + ".status")).read().strip()
    try:
        body = json.load(open(os.path.join(out, i + ".body"), encoding="utf-8"))
    except Exception:
        body = None
    return st, body

checks, failed = [], False
for i in ids:
    st, body = load(i)
    c = {"id": i, "call": LABEL[i], "status": int(st) if st.isdigit() else st}
    err = body.get("error") if isinstance(body, dict) else None
    if isinstance(err, dict):
        c["error"] = {k: (str(err.get(k))[:200] if err.get(k) is not None else None) for k in ("code", "type", "message")}
    if isinstance(body, dict) and "usage" in body:
        c["usage"] = body["usage"]
    ok2 = st == "200"
    if i in ("C1", "C4", "C7"):
        c["reasoning_effort"] = effort_chat
    if i in DENY:
        c["pass"] = True
        c["note"] = ("denied" if st.startswith("4") else
                     "inconclusive: upstream error" if st.startswith("5") or st == "000" else
                     "not denied: relay scopes per group, not per key/model (01 DEC-10)")
    elif i == "C1":
        u = body.get("usage", {}) if isinstance(body, dict) else {}
        c["pass"] = ok2 and "prompt_tokens" in u and "completion_tokens" in u
        if c["pass"]:
            c["cost_usd_est"] = round(u["prompt_tokens"] * 0.10e-6 + u["completion_tokens"] * 0.50e-6, 8)
            c["model_echoed"] = body.get("model")
    elif i == "C3":
        c["model_requested"] = c3_model
        c["api"] = image_api
        if image_api == "responses":
            c["reasoning_effort"] = effort_img
            items = body.get("output") if isinstance(body, dict) else None
            item = next((o for o in (items or []) if isinstance(o, dict)
                         and o.get("type") == "image_generation_call" and o.get("result")), None)
            b64 = item.get("result") if item else None
            meta = item or {}
            if item:
                item.pop("result", None)
                item.pop("revised_prompt", None)
        else:
            data = body.get("data") if isinstance(body, dict) else None
            entry = data[0] if isinstance(data, list) and data and isinstance(data[0], dict) else {}
            b64 = entry.get("b64_json")
            meta = body if isinstance(body, dict) else {}
            if b64:
                body.pop("data", None)
        png = bool(b64)
        if png:
            open(os.path.join(out, "C3.png"), "wb").write(base64.b64decode(b64))
            json.dump(body, open(os.path.join(out, "C3.body"), "w"))
        for k in ("quality", "size", "output_format"):
            if k in meta:
                c[k] = meta[k]
        if "usage" not in c:
            c["usage"] = "n/a:absent"
        u = c["usage"] if isinstance(c["usage"], dict) else None
        if u:
            d = u.get("input_tokens_details", {})
            c["cost_usd_est_openai_rates"] = round(d.get("text_tokens", 0) * 5e-6 + d.get("image_tokens", 0) * 8e-6
                                                   + u.get("output_tokens", 0) * 30e-6, 6)
        c["pass"] = ok2 and png
    else:
        c["pass"] = ok2
    if not c["pass"] and (not st.isdigit() or st.startswith("5") or st == "000"):
        c["reason"] = "upstream"
    failed |= not c["pass"]
    checks.append(c)
    code = (c.get("error") or {}).get("code") or ""
    print(f"{i} {LABEL[i]} -> {st} {code} {'PASS' if c['pass'] else 'FAIL'}" + (f" ({c['note']})" if "note" in c else ""))

d = json.load(open(rec, encoding="utf-8")) if os.path.exists(rec) else {}
d.setdefault("schema", "openai-access.v1")
d["base_url"] = base
d.setdefault("org", "n/a:relay (01 DEC-9: no OpenAI org or dashboard; spend cap lives in the relay account)")
d.setdefault("projects", "n/a:relay (01 DEC-9)")
prev = {c["id"]: c for c in d.get("checks", [])}
prev.update({c["id"]: c for c in checks})
d["checks"] = [prev[k] for k in sorted(prev)]
json.dump(d, open(rec, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
open(rec, "a").write("\n")
print(f"record: {rec}")
print(f"raw:    {out}")
sys.exit(1 if failed else 0)
PY
