#!/bin/sh
# Start-up validation of the NTS_* configuration (WR13). Runs before the image's
# 20-envsubst-on-templates.sh. Refuses to start (exit 1) on a bad value; the message
# names the variable and, for origin lists, the offending token — never the whole value.
set -eu

ME=$(basename "$0")

fail() {
    echo "$ME: ERROR: $1" >&2
    exit 1
}

# reject_multiline NAME VALUE — grep is line-based: one matching line would validate a whole
# multi-line value, and the remaining lines would reach nginx via envsubst. Reject a newline
# and every control character BEFORE the format check (same approach as the origins list).
reject_multiline() {
    case "$2" in
        *"
"*) fail "$1 contains a newline" ;;
    esac
    if printf '%s' "$2" | LC_ALL=C grep -q '[[:cntrl:]]'; then
        fail "$1 contains a control character"
    fi
}

# --- NTS_CSP_MEDIA_ORIGINS: space-separated origins, empty allowed -----------------
origins="${NTS_CSP_MEDIA_ORIGINS:-}"
if [ -n "$origins" ]; then
    # Reject characters that could break out of the CSP directive / nginx string,
    # before any token handling. Control characters are rejected; a plain space is the
    # only allowed separator. grep is line-based, so a newline is checked separately.
    case "$origins" in
        *"
"*) fail "NTS_CSP_MEDIA_ORIGINS contains a newline" ;;
    esac
    if printf '%s' "$origins" | LC_ALL=C grep -q '[;"'"'"'<>()\\[:cntrl:]]'; then
        fail "NTS_CSP_MEDIA_ORIGINS contains a forbidden character (; \" ' < > ( ) \\ or a control character)"
    fi
    for token in $origins; do
        if ! printf '%s' "$token" | grep -Eq '^https?://[A-Za-z0-9.-]+(:[0-9]{1,5})?$'; then
            fail "NTS_CSP_MEDIA_ORIGINS token is not an http(s) origin: $token"
        fi
    done
fi

# --- NTS_HSTS: empty (dev) or max-age=<n>[; includeSubDomains] ----------------------
hsts="${NTS_HSTS:-}"
if [ -n "$hsts" ]; then
    reject_multiline NTS_HSTS "$hsts"
    printf '%s' "$hsts" | grep -Eq '^max-age=[0-9]+(; includeSubDomains)?$' \
        || fail "NTS_HSTS must match max-age=<seconds>[; includeSubDomains]"
fi

# --- NTS_SPA_FALLBACK: a plain file name --------------------------------------------
fallback="${NTS_SPA_FALLBACK:-}"
if [ -n "$fallback" ]; then
    reject_multiline NTS_SPA_FALLBACK "$fallback"
    printf '%s' "$fallback" | grep -Eq '^[a-zA-Z0-9._-]+$' \
        || fail "NTS_SPA_FALLBACK must match ^[a-zA-Z0-9._-]+\$"
fi

exit 0
