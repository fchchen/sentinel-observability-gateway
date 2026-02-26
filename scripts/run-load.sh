#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 is not installed. Install k6 first, then re-run this script."
  exit 127
fi

BASE_URL="${BASE_URL:-http://localhost:8080}"
RATE="${RATE:-50}"
DURATION="${DURATION:-2m}"
PRE_ALLOCATED_VUS="${PRE_ALLOCATED_VUS:-20}"
MAX_VUS="${MAX_VUS:-200}"
DUPLICATE_PCT="${DUPLICATE_PCT:-5}"
INVALID_PCT="${INVALID_PCT:-2}"
TENANT_COUNT="${TENANT_COUNT:-5}"

TIMESTAMP_UTC="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
STAMP_FILE="$(date -u +"%Y%m%dT%H%M%SZ")"
OUT_DIR="docs/perf/runs"
mkdir -p "$OUT_DIR"

SUMMARY_JSON="$OUT_DIR/k6-summary-$STAMP_FILE.json"
RAW_LOG="$OUT_DIR/k6-output-$STAMP_FILE.log"
LATEST_MD="docs/perf/latest-run.md"

echo "Running k6 load test against $BASE_URL (rate=$RATE, duration=$DURATION)..."
K6_SUMMARY_TREND_STATS="avg,min,med,max,p(90),p(95),p(99)" \
k6 run tests/load/ingest.js \
  -e BASE_URL="$BASE_URL" \
  -e RATE="$RATE" \
  -e DURATION="$DURATION" \
  -e PRE_ALLOCATED_VUS="$PRE_ALLOCATED_VUS" \
  -e MAX_VUS="$MAX_VUS" \
  -e DUPLICATE_PCT="$DUPLICATE_PCT" \
  -e INVALID_PCT="$INVALID_PCT" \
  -e TENANT_COUNT="$TENANT_COUNT" \
  --summary-export "$SUMMARY_JSON" | tee "$RAW_LOG"

node -e '
const fs = require("fs");
const summaryPath = process.argv[1];
const outPath = process.argv[2];
const timestamp = process.argv[3];
const baseUrl = process.argv[4];
const knobs = JSON.parse(process.argv[5]);

const data = JSON.parse(fs.readFileSync(summaryPath, "utf8"));
const m = (name) => (data.metrics && data.metrics[name]) || {};
const httpReqDuration = m("http_req_duration");
const acceptedLatency = m("gateway_accepted_latency_ms");
const httpReqs = m("http_reqs");
const failed = m("http_req_failed");
const acceptRate = m("gateway_accept_rate");
const invalidHandled = m("gateway_invalid_handled_rate");
const rateValue = (x) => x.rate ?? x.value ?? "n/a";

const lines = [
  "# Latest k6 Run",
  "",
  `- Date (UTC): ${timestamp}`,
  `- Base URL: ${baseUrl}`,
  `- Rate: ${knobs.RATE} req/s`,
  `- Duration: ${knobs.DURATION}`,
  `- Duplicate %: ${knobs.DUPLICATE_PCT}`,
  `- Invalid %: ${knobs.INVALID_PCT}`,
  "",
  "## Summary",
  `- Requests/sec: ${httpReqs.rate ?? "n/a"}`,
  `- HTTP failed rate: ${rateValue(failed)}`,
  `- Gateway accepted rate: ${rateValue(acceptRate)}`,
  `- Invalid handled rate: ${rateValue(invalidHandled)}`,
  `- HTTP latency p95 (ms): ${httpReqDuration["p(95)"] ?? "n/a"}`,
  `- HTTP latency p99 (ms): ${httpReqDuration["p(99)"] ?? "n/a"}`,
  `- Accepted latency p95 (ms): ${acceptedLatency["p(95)"] ?? "n/a"}`,
  `- Accepted latency p99 (ms): ${acceptedLatency["p(99)"] ?? "n/a"}`
];

fs.writeFileSync(outPath, lines.join("\n") + "\n", "utf8");
' "$SUMMARY_JSON" "$LATEST_MD" "$TIMESTAMP_UTC" "$BASE_URL" "{\"RATE\":\"$RATE\",\"DURATION\":\"$DURATION\",\"DUPLICATE_PCT\":\"$DUPLICATE_PCT\",\"INVALID_PCT\":\"$INVALID_PCT\"}"

echo "Artifacts:"
echo "- Summary JSON: $SUMMARY_JSON"
echo "- Raw output:   $RAW_LOG"
echo "- Snapshot MD:  $LATEST_MD"
