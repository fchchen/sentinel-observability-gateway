#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

SUMMARY_FILE="$(ls -1t docs/perf/runs/k6-summary-*.json 2>/dev/null | head -1 || true)"
if [[ -z "$SUMMARY_FILE" ]]; then
  echo "No k6 summary files found under docs/perf/runs/"
  exit 1
fi

node - "$SUMMARY_FILE" <<'NODE'
const fs = require("fs");
const { execSync } = require("child_process");

const summaryFile = process.argv[2];
const summary = JSON.parse(fs.readFileSync(summaryFile, "utf8"));
const latestRunPath = "docs/perf/latest-run.md";

function extractKnobsFromLatestRun() {
  try {
    const text = fs.readFileSync(latestRunPath, "utf8");
    const get = (label, fallback = "n/a") => {
      const match = text.match(new RegExp(`- ${label}:\\s*(.+)`));
      return match ? match[1].trim() : fallback;
    };

    return {
      baseUrl: get("Base URL", "http://127.0.0.1:8080"),
      rate: get("Rate", "n/a").replace(/\s*req\/s\s*$/i, ""),
      duration: get("Duration", "n/a"),
      duplicatePct: get("Duplicate %", "n/a"),
      invalidPct: get("Invalid %", "n/a")
    };
  } catch {
    return {
      baseUrl: "http://127.0.0.1:8080",
      rate: "n/a",
      duration: "n/a",
      duplicatePct: "n/a",
      invalidPct: "n/a"
    };
  }
}

function metric(name) {
  return (summary.metrics && summary.metrics[name]) || {};
}

function num(value, digits = 2) {
  if (value === null || value === undefined || Number.isNaN(Number(value))) {
    return "n/a";
  }
  return Number(value).toFixed(digits);
}

function fetchJson(url) {
  try {
    const raw = execSync(`curl -s "${url}"`, { encoding: "utf8" });
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function promQuery(expr) {
  const url = `http://127.0.0.1:19090/api/v1/query?query=${encodeURIComponent(expr)}`;
  const payload = fetchJson(url);
  if (!payload || payload.status !== "success" || !Array.isArray(payload.data?.result) || payload.data.result.length === 0) {
    return null;
  }

  const value = payload.data.result[0]?.value?.[1];
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function gitCommit() {
  try {
    return execSync("git rev-parse --short HEAD", { encoding: "utf8" }).trim();
  } catch {
    return "n/a";
  }
}

const httpReqs = metric("http_reqs");
const httpDuration = metric("http_req_duration");
const acceptedDuration = metric("gateway_accepted_latency_ms");
const failedRate = metric("http_req_failed").rate ?? metric("http_req_failed").value;
const acceptedRate = metric("gateway_accept_rate").rate ?? metric("gateway_accept_rate").value;
const invalidHandledRate = metric("gateway_invalid_handled_rate").rate ?? metric("gateway_invalid_handled_rate").value;

const freshnessP95 = promQuery("histogram_quantile(0.95, sum(rate(end_to_end_freshness_seconds_bucket[5m])) by (le))");
const freshnessP99 = promQuery("histogram_quantile(0.99, sum(rate(end_to_end_freshness_seconds_bucket[5m])) by (le))");
const lagAvg = promQuery("avg(processor_lag_seconds)");
const dlqGrowth10m = promQuery("sum(increase(dlq_events_total[10m]))");

const pipelineHealth = fetchJson("http://127.0.0.1:8081/v1/pipeline/health");

const nowUtc = new Date().toISOString();
const knobs = extractKnobsFromLatestRun();
const lagAvgStr = num(lagAvg, 2);
const pipelineLagStr = num(pipelineHealth?.lagSeconds, 2);
const report = `# Load Test Results

## Test metadata
- Date: ${nowUtc}
- Commit: ${gitCommit()}
- Operator: codex
- Environment: local docker + dotnet
- Base URL: ${knobs.baseUrl}

## k6 command
\`\`\`bash
BASE_URL=${knobs.baseUrl} \\
RATE=${knobs.rate} \\
DURATION=${knobs.duration} \\
DUPLICATE_PCT=${knobs.duplicatePct} \\
INVALID_PCT=${knobs.invalidPct} \\
PRE_ALLOCATED_VUS=20 \\
MAX_VUS=120 \\
TENANT_COUNT=5 \\
./scripts/run-load.sh
\`\`\`

## Scenario knobs
- \`RATE\` (events/sec): ${knobs.rate}
- \`DURATION\`: ${knobs.duration}
- \`PRE_ALLOCATED_VUS\`: 20
- \`MAX_VUS\`: 120
- \`DUPLICATE_PCT\`: ${knobs.duplicatePct}
- \`INVALID_PCT\`: ${knobs.invalidPct}
- \`TENANT_COUNT\`: 5

## Summary metrics
- Max sustained events/sec: ${num(httpReqs.rate, 2)}
- Gateway p95 latency (ms): ${num(httpDuration["p(95)"], 2)}
- Gateway p99 latency (ms): ${num(httpDuration["p(99)"], 2)}
- End-to-end freshness p95 (s): ${num(freshnessP95, 2)}
- End-to-end freshness p99 (s): ${num(freshnessP99, 2)}
- Worker lag behavior: avg lag=${lagAvgStr === "n/a" ? "n/a" : `${lagAvgStr}s`}, pipeline lag=${pipelineLagStr === "n/a" ? "n/a" : `${pipelineLagStr}s`}
- DLQ growth during test: increase_10m=${num(dlqGrowth10m, 0)}, total_dlq=${pipelineHealth?.dlqEvents ?? "n/a"}

## Observed bottlenecks
- HTTP failed rate=${num(failedRate, 4)} (expected mostly injected invalid requests).
- Accepted rate=${num(acceptedRate, 4)}, invalid-handled rate=${num(invalidHandledRate, 4)}.
- Gateway accepted latency p95=${num(acceptedDuration["p(95)"], 2)}ms, p99=${num(acceptedDuration["p(99)"], 2)}ms.

## Notes / follow-ups
- Source summary file: \`${summaryFile}\`
- Re-run with higher \`RATE\` (e.g., 60/100) for saturation characterization.
`;

fs.writeFileSync("docs/perf/results.md", report, "utf8");
console.log("Updated docs/perf/results.md from", summaryFile);
NODE
