#!/bin/sh
# Chooses the API routing mode and writes /etc/nginx/conf.d/nts-api-guard.inc, which
# default.conf includes BEFORE `location /`. Runs before the image's
# 20-envsubst-on-templates.sh (scripts run in lexical order); no other behaviour.
#   NTS_API_UPSTREAM set   -> dev/compose reverse proxy for /api, /health, /hubs
#   NTS_API_UPSTREAM empty -> hard 404 (never HTML) for /api, /hubs, /health
set -eu

ME=$(basename "$0")
# .inc, not .conf: the image's nginx.conf does `include /etc/nginx/conf.d/*.conf` at http
# level, where a `location` is invalid; default.conf includes this file inside `server`.
OUT=/etc/nginx/conf.d/nts-api-guard.inc
GUARDS=/etc/nginx/guards

if [ -n "${NTS_API_UPSTREAM:-}" ]; then
    # Only a plain http(s) origin is accepted: it lands inside proxy_pass.
    printf '%s' "$NTS_API_UPSTREAM" | grep -Eq '^https?://[A-Za-z0-9.-]+(:[0-9]{1,5})?$' || {
        echo "$ME: ERROR: NTS_API_UPSTREAM must be an http(s) origin without path" >&2
        exit 1
    }
    envsubst '${NTS_API_UPSTREAM}' < "$GUARDS/api-proxy.conf.template" > "$OUT"
    echo "$ME: API proxy mode (NTS_API_UPSTREAM set): /api, /health, /hubs are proxied"
else
    cp "$GUARDS/api-guard-404.conf" "$OUT"
    echo "$ME: API guard mode (NTS_API_UPSTREAM empty): /api, /hubs, /health return 404"
fi
