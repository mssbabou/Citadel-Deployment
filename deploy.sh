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

# Validate required variables
for var in AUTH_TOKEN DEPLOY_URL PROFILE; do
    if [ -z "${!var:-}" ]; then
        echo "Error: $var not set in .env"
        exit 1
    fi
done

# SOURCE: CLI arg → SOURCE from .env → current directory
SOURCE="${1:-${SOURCE:-.}}"
TEMP_DIR="${TEMP_DIR:-/tmp}"

# Validate source exists
if [ ! -e "$SOURCE" ]; then
    echo "Error: source path does not exist: $SOURCE"
    exit 1
fi

# Create temp zip
TEMP_ZIP=$(mktemp --suffix=.zip --tmpdir="$TEMP_DIR")
rm -f "$TEMP_ZIP"   # zip won't overwrite an existing file cleanly
trap "rm -f $TEMP_ZIP" EXIT

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

# Deploy
echo "🚀 Deploying to $DEPLOY_URL (profile: $PROFILE)"
RESPONSE=$(curl -s -w "\n%{http_code}" -X POST \
    -H "Authorization: Bearer $AUTH_TOKEN" \
    -H "X-Profile: $PROFILE" \
    -F "file=@$TEMP_ZIP" \
    "$DEPLOY_URL")

HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | head -n-1)

if [ "$HTTP_CODE" = "200" ]; then
    echo "✓ Deploy successful!"
    echo "  Response: $BODY"
else
    echo "✗ Deploy failed with HTTP $HTTP_CODE"
    echo "  Response: $BODY"
    exit 1
fi
