# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Sentinel Observability Gateway is an enterprise event ingestion and processing system built with .NET 10, Kafka, PostgreSQL, and Angular 17. It demonstrates event-driven architecture with idempotent processing, full OpenTelemetry observability, SLO-backed alerting, and real-time dashboards.

## Build & Run Commands

### Infrastructure (required first)
```bash
docker compose up -d   # Kafka (KRaft), PostgreSQL, OTel Collector, Prometheus, Grafana, Tempo
```

### .NET Services
```bash
dotnet restore SentinelObservabilityGateway.slnx
dotnet build SentinelObservabilityGateway.slnx

# Run individual services (each in its own terminal)
dotnet run --project src/gateway-api          # :8080
dotnet run --project src/processor-worker     # background worker, no HTTP port
dotnet run --project src/query-api            # :8081
dotnet run --project src/realtime-hub         # :8082
```

### Angular Frontend
```bash
cd src/web-ui && npm install && npm start     # :4200
cd src/web-ui && npx ng test                  # Karma/Jasmine tests
```

### Load Testing (k6)
```bash
BASE_URL=http://localhost:8080 RATE=50 DURATION=2m ./scripts/run-load.sh
```
Configurable env vars: `RATE`, `DURATION`, `DUPLICATE_PCT`, `INVALID_PCT`, `TENANT_COUNT`.

## Architecture

### Data Flow
```
Client → Gateway API (POST /v1/events) → Kafka (events.raw.v1) → Processor Worker → PostgreSQL
                                                                        ↓
                                                                  Realtime Hub (SignalR) → Web UI
                                                                        ↑
                                                              Query API (GET /v1/events/recent)
```

### Four .NET Services
- **gateway-api** — HTTP ingestion endpoint. Validates events, enforces idempotency via `Idempotency-Key` header with SHA-256 payload hash conflict detection, publishes to Kafka. Returns 202 Accepted or 409 Conflict.
- **processor-worker** — BackgroundService consuming from Kafka. Deduplicates via `processed_events` table, persists to `events`, updates `stream_state`, routes poison messages to `dead_letter`, and pushes to realtime-hub.
- **query-api** — Read-only queries against PostgreSQL (`/v1/pipeline/health`, `/v1/events/recent`).
- **realtime-hub** — SignalR hub at `/hubs/events`. Receives processed events from worker via HTTP POST at `/v1/realtime/publish` and broadcasts to connected clients.

### Database Tables (PostgreSQL)
`events`, `idempotency`, `stream_state`, `dead_letter`, `processed_events` — all tenant-scoped with composite indexes on (tenant_id, timestamp) patterns.

### Observability Stack
- **Traces:** OpenTelemetry with W3C Trace Context propagation through Kafka headers. Activity sources per service (e.g., `sentinel-gateway-api`).
- **Metrics:** Custom Prometheus counters/histograms (`gateway_requests_total`, `processor_events_total`, `processor_lag_seconds`, `dlq_events_total`, `end_to_end_freshness_seconds`).
- **Alerts:** Burn-rate SLO alerts in `infra/prometheus/alerts.yml` (5xx ratio, freshness p99, lag, DLQ growth).
- **Dashboards:** Grafana auto-provisioned from `infra/grafana/dashboards/sentinel-overview.json`.

### Key Ports
| Service | Host Port |
|---------|-----------|
| Gateway API | 8080 |
| Query API | 8081 |
| Realtime Hub | 8082 |
| Angular UI | 4200 |
| PostgreSQL | 55432 |
| Kafka | 29092 |
| Prometheus | 9090 |
| Grafana | 3000 (admin/admin) |
| OTel Collector (OTLP) | 14317 |

## Code Conventions

- .NET 10 with nullable reference types and implicit usings enabled globally via `Directory.Build.props`.
- Solution uses the modern `.slnx` format (`SentinelObservabilityGateway.slnx`).
- Each service follows the same internal structure: `Contracts/`, `Data/`, `Options/`, `Observability/`.
- Kafka message key format: `tenantId|streamKey`.
- All telemetry follows the pattern: `*Telemetry.cs` defines ActivitySource/Meter, `*Metrics.cs` records measurements.
- Database schema is initialized in code (processor-worker `Program.cs` runs CREATE TABLE IF NOT EXISTS on startup).

## Reference Documentation
- Full technical spec: `sentinel-observability-gateway_SPEC.md`
- Operational runbooks: `docs/runbooks/` (lag-spike, elevated-5xx, dlq-growth, db-saturation)
- Performance results: `docs/perf/`
