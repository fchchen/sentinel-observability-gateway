# Load Test Results (Example Filled Run)

This file is an example of a completed report shape. Values below are illustrative and not authoritative for your environment.

## Test metadata
- Date: 2026-02-26T18:40:00Z
- Commit: `example`
- Operator: `local-dev`
- Environment: Docker local stack
- Base URL: `http://localhost:8080`

## k6 command
```bash
BASE_URL=http://localhost:8080 \
RATE=75 \
DURATION=5m \
DUPLICATE_PCT=7 \
INVALID_PCT=3 \
./scripts/run-load.sh
```

## Scenario knobs
- `RATE` (events/sec): 75
- `DURATION`: 5m
- `PRE_ALLOCATED_VUS`: 30
- `MAX_VUS`: 300
- `DUPLICATE_PCT`: 7
- `INVALID_PCT`: 3
- `TENANT_COUNT`: 8

## Summary metrics
- Max sustained events/sec: 74.8
- Gateway p95 latency (ms): 182.4
- Gateway p99 latency (ms): 311.7
- End-to-end freshness p95 (s): 3.6
- End-to-end freshness p99 (s): 7.9
- Worker lag behavior: peaking at 4.2s during warmup, stabilized below 1.5s
- DLQ growth during test: 0

## Observed bottlenecks
- Kafka warmup produced a short initial latency spike.

## Notes / follow-ups
- Increase load to 100 req/s and compare p99 + lag trend.
