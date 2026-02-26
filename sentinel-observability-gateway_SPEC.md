# Sentinel Observability Gateway — Project Spec (Codex-ready)

**Repo slug:** `sentinel-observability-gateway`  
**Tagline:** Event-driven ingress for service/domain events with idempotent processing, real-time Angular dashboards, and SLO-backed observability (OpenTelemetry + Grafana).

---

## 1) Purpose

Enterprises don’t lack events—they lack a **reliable, observable control point**. This gateway standardizes event ingestion, protects downstream systems with **idempotency + DLQ**, and ships with **production-grade telemetry** (metrics, logs, traces) plus **SLOs + burn-rate alerts**.

---

## 2) Goals

### Functional
- Ingest service/domain events through a single gateway API.
- Guarantee **idempotent ingestion** (duplicate-safe) and **exactly-once-ish effects** in processing.
- Provide **real-time** and **historical** visibility into events and pipeline health.
- Provide **SLOs** (availability + freshness) with burn-rate alerting.

### Non-functional
- One-command local environment: `docker compose up`.
- Load testing included (k6) with documented p95/p99.
- “Prod-like” operability: dashboards, alert rules, runbooks, DLQ visibility.

---

## 3) Target Users / Personas
- **SRE/Ops:** monitor lag, errors, freshness, burn rate; triage fast.
- **Backend Engineer:** reliable ingestion + debugging via `traceId`.
- **Platform Engineer:** reusable gateway pattern for teams.

---

## 4) High-level Architecture

**Data flow (happy path):**  
Client → **Gateway API** → **Kafka** → **Processor Worker** → **Postgres Hot Store** → **SignalR** → **Angular UI**

**Failure handling:**
- Duplicate events handled by `Idempotency-Key`.
- Transient failures retried.
- Poison events routed to **DLQ** with reason + snapshot.
- End-to-end debug via `traceId` (OpenTelemetry traces).

---

## 5) Tech Stack (enterprise-standard)
- **Backend:** .NET (ASP.NET Core + Worker Service)
- **Event bus:** Kafka (local Docker Compose)
- **Database:** PostgreSQL (hot store; JSONB payloads)
- **Realtime:** SignalR
- **Frontend:** Angular
- **Observability:** OpenTelemetry Collector + Prometheus + Grafana + Tempo (or Jaeger)

---

## 6) Repository Layout (target)

```
src/
  gateway-api/          # ASP.NET Core: POST /v1/events
  processor-worker/     # .NET Worker: Kafka consumer → DB writer → DLQ routing
  query-api/            # ASP.NET Core: query endpoints + pipeline health + SLOs
  realtime-hub/         # ASP.NET Core SignalR hub
  web-ui/               # Angular UI
infra/
  docker-compose.yml
  otel-collector/
  prometheus/
  grafana/
tests/
  load/                 # k6 scripts
docs/
  architecture/
  runbooks/
  perf/
```

---

## 7) Event Contract

### Endpoint
`POST /v1/events`

### Required headers
- `Authorization: Bearer <token>`
- `Idempotency-Key: <string>`
- Optional: `traceparent` (W3C)

### Event envelope (canonical)
```json
{
  "eventId": "b2c6f1a9-2c2a-4d2d-9b7b-0b5f2a0a67d1",
  "tenantId": "contoso",
  "source": "orders-api",
  "type": "OrderCreated",
  "timestampUtc": "2026-02-26T14:22:31Z",
  "schemaVersion": 1,
  "streamKey": "order-184922",
  "payload": { "orderId": "184922", "amount": 83.12, "currency": "USD" }
}
```

### Responses
- `202 Accepted` with `{ eventId, receivedAtUtc, traceId }`
- `409 Conflict` if same idempotency key reused with different payload hash
- `400 Bad Request` invalid schema
- `401/403` auth failures or tenant mismatch

### Kafka
- Topic: `events.raw.v1`
- Partition key: `tenantId|streamKey` (order preserved per stream)

---

## 8) Idempotency & Exactly-once-ish Effects

### Ingestion-time idempotency (Gateway API)
1. Compute `payloadHash` (e.g., SHA-256 of canonical JSON bytes).
2. Insert `(tenantId, idempotencyKey, payloadHash)` into `idempotency` table with unique constraint.
3. If unique violation:
   - If hash matches → safe duplicate → return `202`.
   - If hash differs → return `409` (caller misuse).

### Processing-time safety (Worker)
- Dedup key = `eventId` OR `(tenantId, idempotencyKey)`.
- Writes are upsert-friendly (no duplicate effects).
- Poison events → DLQ with reason + snapshot.
- Optional phase 1.5: **Outbox** pattern to publish derived events reliably.

---

## 9) Data Model (PostgreSQL)

### `events`
- `id` UUID PK
- `tenant_id` TEXT
- `source` TEXT
- `type` TEXT
- `stream_key` TEXT
- `timestamp_utc` TIMESTAMPTZ
- `payload_jsonb` JSONB
- `received_utc` TIMESTAMPTZ
- `processed_utc` TIMESTAMPTZ
Indexes:
- `(tenant_id, timestamp_utc desc)`
- `(tenant_id, source, timestamp_utc desc)`
- `(tenant_id, type, timestamp_utc desc)`
- `(tenant_id, stream_key, timestamp_utc desc)`

### `idempotency`
- `tenant_id` TEXT
- `idempotency_key` TEXT
- `payload_hash` TEXT
- `first_seen_utc` TIMESTAMPTZ
PK: `(tenant_id, idempotency_key)`

### `stream_state`
- `tenant_id` TEXT
- `stream_key` TEXT
- `last_seen_utc` TIMESTAMPTZ
- `last_type` TEXT
- `last_payload_jsonb` JSONB
PK: `(tenant_id, stream_key)`

### `dead_letter`
- `id` UUID PK
- `tenant_id` TEXT
- `event_snapshot_jsonb` JSONB
- `reason` TEXT
- `created_utc` TIMESTAMPTZ

### `outbox` (optional v1.5, recommended)
- `id` UUID PK
- `tenant_id` TEXT
- `type` TEXT
- `payload_jsonb` JSONB
- `created_utc` TIMESTAMPTZ
- `published_utc` TIMESTAMPTZ NULL
- `attempt_count` INT

### `slo_samples`
- `window_start_utc` TIMESTAMPTZ
- `window_end_utc` TIMESTAMPTZ
- `ingestion_good` BIGINT
- `ingestion_total` BIGINT
- `freshness_good` BIGINT
- `freshness_total` BIGINT

---

## 10) Observability Requirements (must ship)

### Traces (OpenTelemetry)
Instrument spans across:
- Gateway API: request receive + kafka produce
- Worker: kafka consume + db write + dlq write (+ outbox publish if present)
- Realtime hub: broadcast
Propagate `traceparent` if provided; otherwise start new trace.

Span attributes:
- `tenant.id`, `event.id`, `event.type`, `source`, `streamKey`, `idempotencyKey`

### Metrics (Prometheus)
- `gateway_requests_total{status}`
- `gateway_request_duration_ms` (histogram)
- `processor_events_total{result=success|retry|dlq}`
- `processor_lag_seconds`
- `dlq_events_total`
- `end_to_end_freshness_seconds` (histogram: processed_utc - received_utc)

### Logs
Structured JSON logs including:
- `traceId`, `tenantId`, `eventId`, `idempotencyKey`, `streamKey`, `source`

### Dashboards (Grafana)
Must include:
- Gateway RPS, 5xx rate, p95/p99 latency
- Worker throughput, errors, retries
- Consumer lag + DLQ growth
- Freshness p95/p99
- SLO + burn-rate panels

---

## 11) SLOs (Phase 1)

### SLO A — Ingestion Availability
- **Objective:** 99.9% non-5xx over 30 days
- **SLI:** `good / total` from `gateway_requests_total`
- **Alert:** burn rate > 10x for 5m OR > 2x for 1h

### SLO B — End-to-End Freshness
- **Objective:** 99% events processed within 10 seconds over 7 days
- **SLI:** histogram of `(processed_utc - received_utc)`
- **Alert:** p99 freshness > 20s for 10m OR lag rising steadily

---

## 12) Angular UI Requirements (must-have screens)

1. **Live Stream**
   - Virtualized table of latest events
   - Filters: tenant/source/type
   - Live updates via SignalR

2. **Event Explorer**
   - Time range picker
   - Table + chart (events over time, freshness percentiles)
   - Details drawer with raw JSON + traceId

3. **Pipeline Health**
   - Lag, throughput, DLQ count, error rate
   - Top DLQ reasons

4. **SLO**
   - Compliance %, error budget remaining, burn-rate indicators

---

## 13) Local Demo (expected to work)

### Infra
`docker compose up -d` should start:
- Kafka (and deps), Postgres
- OpenTelemetry Collector
- Prometheus
- Grafana
- Tempo (or Jaeger)

### Services
- `src/gateway-api` on `:8080`
- `src/query-api` on `:8081`
- `src/realtime-hub` on `:8082`
- `src/processor-worker` background
- Angular on `:4200`

### Demo steps
1. Start infra with docker compose.
2. Start .NET services + worker.
3. Start Angular UI.
4. Send events via curl or generator.
5. Show Grafana dashboards + trace drill-down.
6. Show Angular live stream.

---

## 14) Load Testing (k6)
Include `tests/load/ingest.js` supporting:
- target rate (events/sec)
- % duplicates
- invalid events injection

Document results under `docs/perf/results.md`:
- max sustained events/sec (local)
- gateway p95/p99 latency
- freshness p95/p99
- worker lag behavior

---

## 15) MVP Acceptance Criteria (Definition of Done)
- End-to-end flow: API → Kafka → Worker → DB → SignalR → Angular live stream
- Idempotency: duplicate-safe + 409 conflict on key misuse
- DLQ: poison events captured with reason + visible via API/UI
- Traces: single event trace spans across services
- Dashboards: shipped + working
- Alerts: burn-rate + lag alerts configured
- k6: script included and produces meaningful results
- Runbooks: lag spike / elevated 5xx / DLQ growth / DB saturation

---

## 16) Suggested Roadmap (post-MVP)
- Outbox (if not in MVP) + derived events pipeline
- Multi-tenant hardening (Postgres RLS)
- DLQ replay endpoint with safety checks
- Cloud deployment templates (Helm/Terraform)
- Optional adapters: Event Hubs / Kinesis

---

## 17) README Requirements (for repo root)
Include:
- 2-minute demo
- “What this proves” bullets
- architecture diagram (even simple)
- links to dashboards and trace UI
- load test results

