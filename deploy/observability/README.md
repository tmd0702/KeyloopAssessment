# Local Observability

This local-only stack runs the OpenTelemetry Collector, Elasticsearch, Kibana, Tempo, Prometheus, and Grafana. It has no production authentication or TLS; production requires authenticated services, TLS, credentials, and network isolation.

## Start

Start the mocks and Redis, then start the observability services:

```sh
docker compose up -d sales-mock service-mock redis
docker compose -f deploy/observability/docker-compose.observability.yml up -d
```

Run the backend on port `5100` with OTLP sent to the collector. This keeps the Compose stack decoupled from the backend process while Prometheus reaches it through `host.docker.internal`.

```sh
ASPNETCORE_URLS=http://0.0.0.0:5100 \
ASPNETCORE_ENVIRONMENT=Development \
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://localhost:4318/v1/logs \
Jwt__SigningKey='local-observability-signing-key-at-least-32-bytes' \
dotnet run --no-launch-profile --project src/backend/Keyloop.UnifiedDocuments.Api
```

Open Grafana at http://localhost:3000 (`admin` / `admin`), Kibana at http://localhost:5601, Prometheus at http://localhost:9090, Elasticsearch at http://localhost:9200, and Tempo at http://localhost:3200. OTLP is exposed on `localhost:4317` (gRPC) and `localhost:4318` (HTTP). Prometheus scrapes `http://host.docker.internal:5100/metrics` from the container; the backend endpoint is `/metrics`.

Obtain a development token and make requests:

```sh
TOKEN=$(curl -s http://localhost:5100/api/v1/auth/demo-token | jq -r .accessToken)
curl -H "Authorization: Bearer $TOKEN" http://localhost:5100/api/v1/vehicles/COMMERCIAL-001/documents
curl -H "Authorization: Bearer $TOKEN" http://localhost:5100/api/v1/vehicles/SERVICE-DOWN/documents
curl -H "Authorization: Bearer $TOKEN" http://localhost:5100/api/v1/vehicles/SLOW-FLEET-001/documents
```

`COMMERCIAL-001` shows normal parallel provider work, `SLOW-FLEET-001` shows Sales at about two seconds and Service at about four seconds, `SLOW-SERVICE-001` keeps Sales normal while Service takes about ten seconds, and `SERVICE-DOWN` produces a partial result. Repeat `COMMERCIAL-001` to see the initial L1/L2 misses followed by an L1 hit.

Grafana automatically provisions the **Unified Document Service** dashboard and Prometheus/Tempo datasources. In Grafana Explore, select Tempo then filter `service.name = keyloop-unified-documents-api` to see the request trace, overlapping Sales/Service spans, HTTP child spans, and cache spans.

## Kibana logs

The collector converts OpenTelemetry logs to ECS and writes them to `keyloop-unified-documents-logs-ecs*`. `kibana-setup` provisions the **Keyloop Unified Documents Logs (ECS)** data view. Select it in Kibana Discover and use these columns:

- `@timestamp`
- `log.level`
- `message`
- `event.action`
- `provider`
- `duration_ms`
- `document_count`
- `search.status`
- `trace.id`
- `service.name`

`log.level` comes directly from the OpenTelemetry `SeverityText`; it is not independently recalculated. The ECS exporter maps the application event identity to `event.action`, which is the ECS field for an action such as `ProviderRequestFailed` or `DocumentSearchCompleted`.

Useful KQL filters:

```text
service.name : "keyloop-unified-documents-api"
log.level : "Error"
log.level : ("Warning" or "Error" or "Critical")
event.action : "ProviderRequestFailed"
provider : "SERVICE"
search.status : "PARTIAL"
trace.id : "<trace-id>"
```

Use a business event's `trace.id` in Grafana Explore with the Tempo datasource to open the request trace. It contains the aggregate, concurrent provider, HTTP, and cache spans for the same request. ECS data fields may have `.keyword` multifields for exact matching and aggregations; Discover should normally use the readable parent fields listed above.

The application intentionally logs event metadata, timings, source statuses, and trace identifiers. It does not log document payloads, cache keys, or request paths containing VINs.

```sh
docker compose -f deploy/observability/docker-compose.observability.yml down -v
```