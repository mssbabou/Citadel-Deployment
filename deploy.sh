#!/bin/bash
#
# Citadel deployment client.
# Config: .env in the script's directory. Fields: AUTH_TOKEN, DEPLOY_URL, PROFILE, SOURCE (optional).
# See --help.

set -euo pipefail

VERSION="2.0.0"

usage() {
    cat <<'USAGE'
deploy.sh — Citadel deployment client

Usage:
  deploy.sh [source-path]         Deploy a directory, file, or .zip.
  deploy.sh --dry-run [source]    Build + sign the payload and ask the server
                                  what would happen; no mutation is performed.
  deploy.sh --list                List profiles registered on the server.
  deploy.sh --help                Show this help.
  deploy.sh --version             Show version.

Config: .env in the script's directory with AUTH_TOKEN, DEPLOY_URL, PROFILE,
and optional SOURCE. CLI source-path overrides SOURCE.

Environment:
  TEMP_DIR        Directory for intermediate files (default: /tmp).
  DEPLOY_QUIET    If set to 1, suppress banners (errors still print).
USAGE
}

QUIET="${DEPLOY_QUIET:-0}"
DRY_RUN=0
LIST=0
CLI_SOURCE=""

while [ $# -gt 0 ]; do
    case "$1" in
        --help|-h) usage; exit 0 ;;
        --version|-v) echo "deploy.sh $VERSION"; exit 0 ;;
        --dry-run) DRY_RUN=1; shift ;;
        --list) LIST=1; shift ;;
        --quiet|-q) QUIET=1; shift ;;
        --) shift; break ;;
        -*) echo "Error: unknown flag: $1" >&2; echo "Try 'deploy.sh --help'" >&2; exit 2 ;;
        *) CLI_SOURCE="$1"; shift ;;
    esac
done

say() { [ "$QUIET" = "1" ] || printf '%s\n' "$*"; }
die() { printf '%s\n' "$*" >&2; exit 1; }

# ─── Load .env (safe parser — no source/eval) ─────────────────────────────────

ENV_FILE="$(dirname "$0")/.env"
[ -f "$ENV_FILE" ] || die "Error: .env file not found at $ENV_FILE"

AUTH_TOKEN=""; DEPLOY_URL=""; PROFILE=""; SOURCE=""
while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in
        ''|'#'*) continue ;;
    esac
    key="${line%%=*}"
    value="${line#*=}"
    [ "$key" = "$line" ] && continue   # no '='; skip
    # Trim leading/trailing whitespace on key; strip matching surrounding quotes on value
    key="${key#"${key%%[![:space:]]*}"}"
    key="${key%"${key##*[![:space:]]}"}"
    case "$value" in
        \"*\") value="${value#\"}"; value="${value%\"}" ;;
        \'*\') value="${value#\'}"; value="${value%\'}" ;;
    esac
    case "$key" in
        AUTH_TOKEN) AUTH_TOKEN="$value" ;;
        DEPLOY_URL) DEPLOY_URL="$value" ;;
        PROFILE)    PROFILE="$value" ;;
        SOURCE)     SOURCE="$value" ;;
    esac
done < "$ENV_FILE"

[ -n "$CLI_SOURCE" ] && SOURCE="$CLI_SOURCE"

[ -n "$AUTH_TOKEN" ] || die "Error: AUTH_TOKEN not set in $ENV_FILE"
[ -n "$DEPLOY_URL" ] || die "Error: DEPLOY_URL not set in $ENV_FILE"
[ -n "$PROFILE"    ] || die "Error: PROFILE not set in $ENV_FILE"

ROOT_URL="${DEPLOY_URL%/deploy}"
TEMP_DIR="${TEMP_DIR:-/tmp}"

sign_nobody() {
    # $1 = context string ("GET /profiles" etc.); $2 = unix timestamp
    printf '%s\n%s\n' "$2" "$1" | openssl dgst -sha256 -hmac "$AUTH_TOKEN" -binary | xxd -p -c 256
}

# ─── --list ────────────────────────────────────────────────────────────────────

if [ "$LIST" = "1" ]; then
    TS=$(date +%s)
    SIG=$(sign_nobody "GET /profiles" "$TS")
    HTTP_CODE=$(curl -s -o /tmp/citadel_profiles.$$ -w "%{http_code}" \
        -H "X-Protocol: v2" -H "X-Timestamp: $TS" -H "X-Signature: $SIG" \
        "$ROOT_URL/profiles")
    if [ "$HTTP_CODE" = "200" ]; then
        cat /tmp/citadel_profiles.$$
        echo
        rm -f /tmp/citadel_profiles.$$
        exit 0
    fi
    cat /tmp/citadel_profiles.$$ >&2
    rm -f /tmp/citadel_profiles.$$
    die "Failed to list profiles: HTTP $HTTP_CODE"
fi

# ─── Deploy / dry-run ─────────────────────────────────────────────────────────

[ -n "$SOURCE" ] || die "Error: SOURCE not set (pass as argument or set in .env)"
[ -e "$SOURCE" ] || die "Error: source path does not exist: $SOURCE"

TEMP_ZIP=$(mktemp --suffix=.zip --tmpdir="$TEMP_DIR")
rm -f "$TEMP_ZIP"   # zip won't cleanly overwrite an existing file
RESP_FILE=$(mktemp)
trap 'rm -f "$TEMP_ZIP" "$RESP_FILE"' EXIT

if [[ "$SOURCE" == *.zip ]]; then
    say "📦 Copying zip file: $SOURCE"
    cp "$SOURCE" "$TEMP_ZIP"
elif [ -d "$SOURCE" ]; then
    say "📦 Zipping directory: $SOURCE"
    (cd "$(dirname "$SOURCE")" && zip -r -q "$TEMP_ZIP" "$(basename "$SOURCE")")
else
    say "📦 Zipping file: $SOURCE"
    zip -q "$TEMP_ZIP" "$SOURCE"
fi

ZIP_SIZE=$(du -h "$TEMP_ZIP" | cut -f1)
say "✓ Created zip: $ZIP_SIZE"

TS=$(date +%s)
SIG=$({ printf '%s\n%s\n' "$TS" "$PROFILE"; cat "$TEMP_ZIP"; } \
    | openssl dgst -sha256 -hmac "$AUTH_TOKEN" -binary | xxd -p -c 256)

CURL_ARGS=(-s --no-buffer -o "$RESP_FILE" -w "%{http_code}" -X POST
    -H "X-Protocol: v2"
    -H "X-Timestamp: $TS"
    -H "X-Signature: $SIG"
    -H "X-Profile: $PROFILE"
    -F "file=@$TEMP_ZIP"
    "$DEPLOY_URL")
if [ "$DRY_RUN" = "1" ]; then
    say "🔍 Dry run — no side effects will be applied"
    CURL_ARGS=(-H "X-DryRun: true" "${CURL_ARGS[@]}")
fi

say "🚀 Deploying to $DEPLOY_URL (profile: $PROFILE)"
HTTP_CODE=$(curl "${CURL_ARGS[@]}")

cat "$RESP_FILE"
[ -z "$(tail -c1 "$RESP_FILE")" ] || echo

if [ "$DRY_RUN" = "1" ]; then
    [ "$HTTP_CODE" = "200" ] || die "DEPLOY FAILED: HTTP $HTTP_CODE"
    say "✓ Dry run OK"
    exit 0
fi

LAST_LINE=$(grep -v '^$' "$RESP_FILE" | tail -n1 || true)
if [ "$HTTP_CODE" = "200" ] && [ "$LAST_LINE" = "OK" ]; then
    say "✓ Done"
    exit 0
fi

die "DEPLOY FAILED: HTTP $HTTP_CODE, last line: ${LAST_LINE:-<empty>}"
