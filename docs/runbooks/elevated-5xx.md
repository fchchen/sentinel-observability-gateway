# Runbook: Elevated Gateway 5xx

## Trigger
- Alert: `GatewayIngestionBurnRateFast` or `GatewayIngestionBurnRateSlow`
- Symptom: elevated `gateway_requests_total{status=~"5.."}`.

## Immediate checks
1. Confirm gateway process health (`/health`).
2. Check error volume and recent deploy/config change.
3. Verify Kafka and Postgres connectivity from gateway host.

## Triage steps
1. Open Grafana panels: `Gateway 5xx Rate`, `Gateway Latency p95/p99`.
2. Inspect gateway logs with `traceId` for top exception patterns.
3. Correlate with dependency failures (Kafka unavailable, DB unavailable).
4. Check if failures are tenant-specific or global.

## Mitigations
1. Roll back recent deploy if regression suspected.
2. Restart gateway instances if stuck connections are detected.
3. Fail open or shed traffic according to policy.
4. If Kafka outage is primary cause, return controlled 503 with clear caller guidance.

## Exit criteria
1. 5xx rate returns to normal background level.
2. Burn-rate alerts clear for both fast and slow windows.
3. Request latency normalizes.

## Post-incident follow-up
1. Record burn-rate duration and user impact.
2. Add regression tests around the triggering failure mode.
3. Tune circuit-breaker/backoff strategy if relevant.
