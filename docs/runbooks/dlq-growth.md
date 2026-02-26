# Runbook: DLQ Growth

## Trigger
- Alert: `DlqGrowthDetected`
- Symptom: `dlq_events_total` increases continuously.

## Immediate checks
1. Confirm the DLQ growth is current (not delayed metrics).
2. Sample recent dead-letter records and group by `reason`.
3. Identify whether one tenant/type dominates failures.

## Triage steps
1. Inspect worker logs for parse/validation/persistence failures.
2. Verify event schema compatibility against current contract.
3. Check Postgres write path for constraint or connectivity issues.
4. Check realtime publish path for systemic failures (should not block commit).

## Mitigations
1. Fix upstream producer sending malformed payloads.
2. Patch worker parser/validation for backward-compatible handling.
3. If DB issue, stabilize DB before replay.
4. Replay DLQ items only after root cause is addressed.

## Exit criteria
1. `increase(dlq_events_total[10m])` returns to zero or expected baseline.
2. New events process successfully without DLQ routing.
3. Replay backlog drains with no re-poisoning.

## Post-incident follow-up
1. Add targeted tests for the observed DLQ reason(s).
2. Add stronger contract validation at ingress if needed.
3. Document replay guardrails and idempotency expectations.
