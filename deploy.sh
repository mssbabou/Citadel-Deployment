#!/bin/bash

# Deploy script for Citadel deployment system
# All configuration is read from .env in the same directory as this script.
# Usage: deploy.sh [source-path]

set -e

# Load .env from same directory as this script
ENV_FILE="$(dirname "$0")/.env"
if [ ! -f "$ENV_FILE" ]; then
    echo "Error: .env file not found at $ENV_FILE"
    exit 1
fi

source "$ENV_FILE"

# CLI arg overrides SOURCE from .env
if [ -n "${1:-}" ]; then SOURCE="$1"; fi

# Validate required variables
for var in AUTH_TOKEN DEPLOY_URL PROFILE SOURCE; do
    if [ -z "${!var:-}" ]; then
        echo "Error: $var not set in .env"
        exit 1
    fi
done

TEMP_DIR="${TEMP_DIR:-/tmp}"

# Validate source exists
if [ ! -e "$SOURCE" ]; then
    echo "Error: source path does not exist: $SOURCE"
    exit 1
fi

# Create temp zip and response file
TEMP_ZIP=$(mktemp --suffix=.zip --tmpdir="$TEMP_DIR")
rm -f "$TEMP_ZIP"   # zip won't overwrite an existing file cleanly
RESP_FILE=$(mktemp)
trap "rm -f \"$TEMP_ZIP\" \"$RESP_FILE\"" EXIT

# Zip the source
if [[ "$SOURCE" == *.zip ]]; then
    echo "📦 Copying zip file: $SOURCE"
    cp "$SOURCE" "$TEMP_ZIP"
elif [ -d "$SOURCE" ]; then
    echo "📦 Zipping directory: $SOURCE"
    (cd "$(dirname "$SOURCE")" && zip -r -q "$TEMP_ZIP" "$(basename "$SOURCE")")
else
    echo "📦 Zipping file: $SOURCE"
    zip -q "$TEMP_ZIP" "$SOURCE"
fi

ZIP_SIZE=$(du -h "$TEMP_ZIP" | cut -f1)
echo "✓ Created zip: $ZIP_SIZE"

# Deploy — stream progress lines as they arrive
echo "🚀 Deploying to $DEPLOY_URL (profile: $PROFILE)"
curl -s --no-buffer -X POST \
    -H "Authorization: Bearer $AUTH_TOKEN" \
    -H "X-Profile: $PROFILE" \
    -F "file=@$TEMP_ZIP" \
    "$DEPLOY_URL" | tee "$RESP_FILE"
echo ""

LAST_LINE=$(tail -n1 "$RESP_FILE")
if [ "$LAST_LINE" = "OK" ]; then
    echo "✓ Done"
else
    exit 1
fi
