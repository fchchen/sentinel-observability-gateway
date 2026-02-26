# Load Test Results

## Test metadata
- Date: 2026-02-26T16:14:35.605Z
- Commit: 5150e29
- Operator: codex
- Environment: local docker + dotnet
- Base URL: http://127.0.0.1:8080

## k6 command
```bash
BASE_URL=http://127.0.0.1:8080 \
RATE=30 \
DURATION=5s \
DUPLICATE_PCT=5 \
INVALID_PCT=2 \
PRE_ALLOCATED_VUS=20 \
MAX_VUS=120 \
TENANT_COUNT=5 \
./scripts/run-load.sh
```

## Scenario knobs
- `RATE` (events/sec): 30
- `DURATION`: 5s
- `PRE_ALLOCATED_VUS`: 20
- `MAX_VUS`: 120
- `DUPLICATE_PCT`: 5
- `INVALID_PCT`: 2
- `TENANT_COUNT`: 5

## Summary metrics
- Max sustained events/sec: 43.44
- Gateway p95 latency (ms): 20.54
- Gateway p99 latency (ms): 25.30
- End-to-end freshness p95 (s): n/a
- End-to-end freshness p99 (s): n/a
- Worker lag behavior: avg lag=n/a, pipeline lag=0.00s
- DLQ growth during test: increase_10m=n/a, total_dlq=0

## Observed bottlenecks
- HTTP failed rate=0.0132 (expected mostly injected invalid requests).
- Accepted rate=1.0000, invalid-handled rate=1.0000.
- Gateway accepted latency p95=20.63ms, p99=25.33ms.

## Notes / follow-ups
- Source summary file: `docs/perf/runs/k6-summary-20260226T161430Z.json`
- Re-run with higher `RATE` (e.g., 60/100) for saturation characterization.
