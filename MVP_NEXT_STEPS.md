# MVP Next Steps

Bootstrap is now in place. Next goal is to complete the end-to-end MVP matching `sentinel-observability-gateway_SPEC.md`.

## Phase 1: Bootstrap Runtime

1. Create `.NET` solution and projects: `DONE`
   - `src/gateway-api` (ASP.NET Core API)
   - `src/processor-worker` (Worker Service)
   - `src/query-api` (ASP.NET Core API)
   - `src/realtime-hub` (ASP.NET Core + SignalR)
2. Add local infra in `infra/docker-compose.yml`: `DONE`
   - Kafka (+ dependency), Postgres
   - OpenTelemetry Collector, Prometheus, Grafana, Tempo/Jaeger
3. Scaffold Angular app in `src/web-ui`: `DONE`

## Phase 2: Core Event Path

1. Implement `POST /v1/events` with:
   - auth placeholder
   - `Idempotency-Key` handling
   - payload validation and envelope mapping
2. Publish accepted events to Kafka topic `events.raw.v1`. `DONE`
3. Consume events in worker, persist to Postgres, and route poison events to DLQ table. `DONE`
4. Broadcast processed events through SignalR. `DONE`

## Phase 3: Observability + SLO

1. Add OpenTelemetry tracing across services. `DONE`
2. Add Prometheus metrics required by spec. `DONE`
3. Build Grafana dashboards and basic burn-rate alert rules. `DONE`
4. Expose query endpoints for pipeline health and SLO snapshots. `PARTIAL` (`/v1/pipeline/health` and `/v1/events/recent` implemented; SLO snapshots pending)

## Phase 4: Verification

1. Add `tests/load/ingest.js` k6 script. `DONE`
2. Run local load tests and document p95/p99 in `docs/perf/results.md`. `PARTIAL` (template created, execution pending)
3. Add runbooks: `DONE`
   - lag spike
   - elevated 5xx
   - DLQ growth
   - DB saturation

## Definition of Progress

- First checkpoint: one event can flow API -> Kafka -> Worker -> DB and appear on a SignalR live feed.
