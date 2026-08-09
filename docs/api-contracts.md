# API Contracts

Sales mock: `GET /api/v1/vehicles/{vin}/documents?page=1&pageSize=50`, authenticated by `Authorization: Bearer <token>`. It returns page-numbered `items`.

Service mock: `GET /api/v2/documents?vehicleVin={vin}&limit=50&cursor=...`, authenticated by `X-API-Key`. It returns cursor-based `records` and `nextCursor`.

Unified REST: `GET /api/v1/vehicles/{vin}/documents`. It returns normalized IDs (`SALES:<id>` or `SERVICE:<id>`), sources, and `COMPLETE` or `PARTIAL` status. Both upstream failures return RFC 7807 ProblemDetails with HTTP 502.

Unified SSE: `GET /api/v1/vehicles/{vin}/documents/stream`. Events are `search.started`, `source.completed`, `source.failed`, and `search.completed`. Source-level payloads contain safe normalized metadata only.

Mock scenarios use `?scenario=normal|slow|empty|rate-limited|unavailable|timeout`; missing or invalid credentials return 401. Upstream paging remains internal to adapters.