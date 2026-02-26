#!/usr/bin/env bash
# Capture high-quality screenshots of the running Sentinel Observability Gateway.
#
# Prerequisites (must all be running):
#   docker compose up -d
#   dotnet run --project src/gateway-api
#   dotnet run --project src/processor-worker
#   dotnet run --project src/query-api
#   dotnet run --project src/realtime-hub
#   cd src/web-ui && npm start
#
# Usage:
#   ./scripts/capture-screenshots.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")/screenshots" && pwd)"

echo "==> Installing dependencies..."
cd "$SCRIPT_DIR"
npm install --silent

echo "==> Installing Chromium browser..."
npx playwright install chromium

echo "==> Capturing screenshots..."
npx tsx capture.ts

echo "==> Done! Screenshots saved to docs/screenshots/"
