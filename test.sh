#!/bin/bash
# test.sh — run all tests: .NET unit tests + deploy.sh integration tests
#
# Usage:
#   bash test.sh               # run everything
#   TEST_PORT=19091 bash test.sh  # use a different port for the integration server

set -uo pipefail

# ─── Utilities ───────────────────────────────────────────────────────────────

RED='\033[0;31m'
GREEN='\033[0;32m'
BOLD='\033[1m'
RESET='\033[0m'

PASS=0
FAIL=0

pass() { printf "  ${GREEN}PASS${RESET}: %s\n" "$1"; PASS=$((PASS + 1)); }
fail() { printf "  ${RED}FAIL${RESET}: %s — %s\n" "$1" "$2"; FAIL=$((FAIL + 1)); }

require_cmd() {
    if ! command -v "$1" &>/dev/null; then
        printf "${RED}error${RESET}: required command not found: %s\n" "$1" >&2
        exit 1
    fi
}

require_cmd dotnet
require_cmd zip
require_cmd curl

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# ─── Phase 1: .NET unit tests ─────────────────────────────────────────────────

printf "\n${BOLD}── .NET unit tests${RESET}\n"
UNIT_EXIT=0
dotnet test server/CitadelServer.sln --logger "console;verbosity=normal" --nologo || UNIT_EXIT=$?
if [ "$UNIT_EXIT" -eq 0 ]; then
    pass ".NET unit tests"
else
    fail ".NET unit tests" "see output above"
fi

# ─── Phase 2: deploy.sh integration tests ─────────────────────────────────────

printf "\n${BOLD}── deploy.sh integration tests${RESET}\n"

TOKEN="test-integ-token-$(date +%s)"
PORT="${TEST_PORT:-19090}"
DEPLOY_DIR=$(mktemp -d)
CONFIG_DIR=$(mktemp -d)
SERVER_PID=""
ENV_BACKUP=""

# Back up any existing .env so we can restore it on exit
if [ -f .env ]; then
    ENV_BACKUP=$(mktemp)
    cp .env "$ENV_BACKUP"
fi

cleanup() {
    if [ -n "$SERVER_PID" ]; then
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
    fi
    rm -rf "$DEPLOY_DIR" "$CONFIG_DIR"
    if [ -n "$ENV_BACKUP" ] && [ -f "$ENV_BACKUP" ]; then
        mv "$ENV_BACKUP" .env
    else
        rm -f .env
    fi
}
trap cleanup EXIT

write_env() {
    local token="$1" profile="$2"
    cat > .env << EOF
AUTH_TOKEN=$token
DEPLOY_URL=http://localhost:$PORT/deploy
PROFILE=$profile
EOF
}

# Build server so artifacts are in place for --no-build
printf "  Building server..."
BUILD_EXIT=0
dotnet build server/CitadelServer -c Debug --nologo -q || BUILD_EXIT=$?
if [ "$BUILD_EXIT" -ne 0 ]; then
    printf "\n"
    fail "server build" "dotnet build failed"
    printf "\n${BOLD}─────────────────────────────────────────────${RESET}\n"
    printf "Results: ${GREEN}%d passed${RESET}, ${RED}%d failed${RESET}\n" "$PASS" "$FAIL"
    exit 1
fi
printf " done\n"

# Write config.toml into the Debug output dir (AppContext.BaseDirectory for dotnet run)
cat > "$CONFIG_DIR/config.toml" << EOF
[server]
token = "$TOKEN"
port = $PORT

[profiles.testprofile]
deploy_dir = "$DEPLOY_DIR"
services = []
EOF
cp "$CONFIG_DIR/config.toml" server/CitadelServer/bin/Debug/net10.0/config.toml

# Start server in background
dotnet run --project server/CitadelServer --no-build -c Debug > /dev/null 2>&1 &
SERVER_PID=$!

# Poll until server responds (up to 15 s)
printf "  Waiting for server"
READY=false
for i in $(seq 1 30); do
    HTTP=$(curl -s -o /dev/null -w "%{http_code}" -X POST "http://localhost:$PORT/deploy" 2>/dev/null) || HTTP="000"
    if [ "$HTTP" = "401" ] || [ "$HTTP" = "400" ] || [ "$HTTP" = "405" ]; then
        printf " ready\n"
        READY=true
        break
    fi
    printf "."
    sleep 0.5
done

if [ "$READY" = "false" ]; then
    printf " TIMEOUT\n"
    fail "server startup" "did not respond within 15 s"
    printf "\n${BOLD}─────────────────────────────────────────────${RESET}\n"
    printf "Results: ${GREEN}%d passed${RESET}, ${RED}%d failed${RESET}\n" "$PASS" "$FAIL"
    exit 1
fi

# ── Test 1: directory deploy ─────────────────────────────────────────────────
SRC=$(mktemp -d)
mkdir -p "$SRC/sub"
echo "hello" > "$SRC/hello.txt"
echo "world" > "$SRC/sub/world.txt"
write_env "$TOKEN" "testprofile"
DEPLOY_EXIT=0; ./deploy.sh "$SRC" > /dev/null 2>&1 || DEPLOY_EXIT=$?
if [ "$DEPLOY_EXIT" -eq 0 ] && [ -f "$DEPLOY_DIR/hello.txt" ] && [ -f "$DEPLOY_DIR/sub/world.txt" ]; then
    pass "directory deploy"
elif [ "$DEPLOY_EXIT" -ne 0 ]; then
    fail "directory deploy" "deploy.sh exited $DEPLOY_EXIT"
else
    fail "directory deploy" "expected files not found in deploy dir"
fi
rm -rf "$SRC"

# ── Test 2: pre-made zip deploy ──────────────────────────────────────────────
SRC=$(mktemp -d)
echo "zipped content" > "$SRC/zipped.txt"
ZIP_DIR=$(mktemp -d)
ZIP="$ZIP_DIR/test.zip"   # don't pre-create the file; zip won't cleanly overwrite an existing file
(cd "$SRC" && zip -r "$ZIP" . > /dev/null)
rm -rf "$SRC"
write_env "$TOKEN" "testprofile"
DEPLOY_EXIT=0; ./deploy.sh "$ZIP" > /dev/null 2>&1 || DEPLOY_EXIT=$?
if [ "$DEPLOY_EXIT" -eq 0 ] && [ -f "$DEPLOY_DIR/zipped.txt" ]; then
    pass "pre-made zip deploy"
elif [ "$DEPLOY_EXIT" -ne 0 ]; then
    fail "pre-made zip deploy" "deploy.sh exited $DEPLOY_EXIT"
else
    fail "pre-made zip deploy" "file not found in deploy dir"
fi
rm -rf "$ZIP_DIR"

# ── Test 3: single file deploy ───────────────────────────────────────────────
FILE=$(mktemp --suffix=.conf)
echo "single file content" > "$FILE"
BASENAME=$(basename "$FILE")
write_env "$TOKEN" "testprofile"
DEPLOY_EXIT=0; ./deploy.sh "$FILE" > /dev/null 2>&1 || DEPLOY_EXIT=$?
if [ "$DEPLOY_EXIT" -eq 0 ] && [ -f "$DEPLOY_DIR/$BASENAME" ]; then
    pass "single file deploy"
elif [ "$DEPLOY_EXIT" -ne 0 ]; then
    fail "single file deploy" "deploy.sh exited $DEPLOY_EXIT"
else
    fail "single file deploy" "file not found in deploy dir"
fi
rm -f "$FILE"

# ── Test 4: second deploy replaces first ─────────────────────────────────────
SRC1=$(mktemp -d); echo "old" > "$SRC1/old.txt"
write_env "$TOKEN" "testprofile"
./deploy.sh "$SRC1" > /dev/null 2>&1 || true

SRC2=$(mktemp -d); echo "new" > "$SRC2/new.txt"
DEPLOY_EXIT=0; ./deploy.sh "$SRC2" > /dev/null 2>&1 || DEPLOY_EXIT=$?
if [ "$DEPLOY_EXIT" -eq 0 ] && [ ! -f "$DEPLOY_DIR/old.txt" ] && [ -f "$DEPLOY_DIR/new.txt" ]; then
    pass "second deploy replaces first"
elif [ "$DEPLOY_EXIT" -ne 0 ]; then
    fail "second deploy replaces first" "deploy.sh exited $DEPLOY_EXIT"
else
    fail "second deploy replaces first" "old file still present or new file missing"
fi
rm -rf "$SRC1" "$SRC2"

# ── Test 5: wrong token exits non-zero ───────────────────────────────────────
SRC=$(mktemp -d); echo "x" > "$SRC/x.txt"
write_env "wrong-token-xyz" "testprofile"
DEPLOY_EXIT=0; ./deploy.sh "$SRC" > /dev/null 2>&1 || DEPLOY_EXIT=$?
if [ "$DEPLOY_EXIT" -ne 0 ]; then
    pass "wrong token exits non-zero"
else
    fail "wrong token exits non-zero" "expected non-zero exit, got 0"
fi
rm -rf "$SRC"

# ── Test 6: unknown profile exits non-zero ────────────────────────────────────
SRC=$(mktemp -d); echo "x" > "$SRC/x.txt"
write_env "$TOKEN" "no-such-profile"
DEPLOY_EXIT=0; ./deploy.sh "$SRC" > /dev/null 2>&1 || DEPLOY_EXIT=$?
if [ "$DEPLOY_EXIT" -ne 0 ]; then
    pass "unknown profile exits non-zero"
else
    fail "unknown profile exits non-zero" "expected non-zero exit, got 0"
fi
rm -rf "$SRC"

# ── Test 7: missing source exits non-zero ────────────────────────────────────
write_env "$TOKEN" "testprofile"
DEPLOY_EXIT=0; ./deploy.sh "/tmp/nonexistent-citadel-test-src-$$" > /dev/null 2>&1 || DEPLOY_EXIT=$?
if [ "$DEPLOY_EXIT" -ne 0 ]; then
    pass "missing source exits non-zero"
else
    fail "missing source exits non-zero" "expected non-zero exit, got 0"
fi

# ─── Summary ──────────────────────────────────────────────────────────────────

printf "\n${BOLD}─────────────────────────────────────────────${RESET}\n"
if [ "$FAIL" -eq 0 ]; then
    printf "Results: ${GREEN}${BOLD}%d passed${RESET}, %d failed\n" "$PASS" "$FAIL"
    exit 0
else
    printf "Results: ${GREEN}%d passed${RESET}, ${RED}${BOLD}%d failed${RESET}\n" "$PASS" "$FAIL"
    exit 1
fi
