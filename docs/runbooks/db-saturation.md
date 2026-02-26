# Runbook: Database Saturation

## Trigger
- Symptom: rising query latency, connection pressure, or write failures.
- Correlated symptoms: lag increase, retries, DLQ growth.

## Immediate checks
1. Confirm Postgres is reachable and accepting connections.
2. Check active connections vs configured limits.
3. Check lock contention and long-running queries.

## Triage steps
1. Review worker and query API logs for DB timeout/constraint errors.
2. Identify hot queries and index misses on `events` / `processed_events`.
3. Verify disk and IOPS headroom for Postgres volume.
4. Confirm no runaway load test or traffic spike is active.

## Mitigations
1. Increase DB resources or connection limits (short-term).
2. Reduce ingestion pressure temporarily.
3. Add or adjust indexes for hottest query patterns.
4. Move non-critical reads off primary if architecture supports it.

## Exit criteria
1. DB latency and error rate return to baseline.
2. Worker throughput recovers and lag trends down.
3. No sustained timeout bursts in application logs.

## Post-incident follow-up
1. Capture capacity metrics at incident peak.
2. Adjust autoscaling/capacity plan.
3. Add regression load profile that reproduces the bottleneck.
