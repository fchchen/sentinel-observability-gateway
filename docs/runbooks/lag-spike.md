# Runbook: Processor Lag Spike

## Trigger
- Alert: `ProcessorLagRising`
- Symptom: `processor_lag_seconds` climbs and keeps increasing.

## Immediate checks
1. Confirm worker process is running and healthy.
2. Check `processor_events_total{result="success"}` vs `processor_events_total{result="retry|dlq"}`.
3. Check Kafka health and topic traffic (`events.raw.v1`).

## Triage steps
1. Open Grafana panel: `Processor Lag Seconds`.
2. Check if gateway ingress rate exceeds worker throughput.
3. Inspect worker logs for repeated DB or Kafka exceptions.
4. Check Postgres saturation signals (CPU, locks, connections).

## Mitigations
1. Scale worker replicas or increase worker resources.
2. Reduce producer rate (temporary traffic shaping).
3. Resolve blocking dependency (DB, Kafka, network).
4. If poison events are looping, isolate by moving to DLQ aggressively.

## Exit criteria
1. `processor_lag_seconds` returns to normal baseline.
2. Success throughput catches up with ingress.
3. No sustained retry storm in worker logs.

## Post-incident follow-up
1. Record peak lag and recovery time.
2. Capture root cause and permanent fix.
3. Update alert thresholds if needed.
