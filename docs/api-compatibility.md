# Clash API compatibility

The contract is derived from Zashboard 3.24.0 at commit `f6dd9c07e843ab89632cc373522254a1bfe6bbe5` and cross-checked against compatible Mihomo behavior.

## Authentication

- REST: `Authorization: Bearer <secret>`
- WebSocket: `?token=<secret>`
- Base address: `http(s)://host:port[/base-path]`

Dynamic path segments and query values are encoded independently. Credentials are never embedded in the backend address.

## REST surface

| Area | Methods and paths |
| --- | --- |
| Version | `GET /version` |
| Proxies | `GET /proxies`, `PUT/DELETE /proxies/{group}` |
| Delay | `GET /proxies/{node}/delay`, `GET /group/{group}/delay` |
| Proxy providers | `GET /providers/proxies`, `PUT /providers/proxies/{name}`, provider health checks |
| Rules | `GET /rules`, `PATCH /rules/disable`, `PUT /rules/{uuid}` |
| Rule providers | `GET /providers/rules`, `PUT /providers/rules/{name}` |
| Connections | `DELETE /connections`, `DELETE /connections/{id}`, Smart block extension |
| Configuration | `GET/PATCH/PUT /configs`, listener ports, LAN access, mode, TUN, reload, force update and Geo update |
| Caches and DNS | FakeIP flush, DNS flush, `GET /dns/query` |
| Maintenance | Core upgrade, restart and dashboard upgrade |
| Storage | `GET/PUT/DELETE /storage/zashboard` |
| Extensions | Smart weights/reset and Honk runtime statistics |

Mutation endpoints accept any successful 2xx status, including `204 No Content`. JSON endpoints require a non-empty compatible payload.

Delay targets prefer the selected group `testUrl`, then provider and node `testUrl` values, and finally `https://www.gstatic.com/generate_204`. Provider-owned nodes use the provider-scoped health-check route; a failed provider route is not silently retried through the global proxy route. The HTTP transport deadline is never shorter than the requested controller timeout plus a five-second response grace period.

Smart actions are response-driven. Weight controls appear only for Smart groups, and a connection can be blocked only when its own `metadata.smartBlock` value is `normal`. A previous successful Smart call does not make unrelated connections eligible.

## WebSocket surface

| Path | Payload |
| --- | --- |
| `/connections` | Active connection snapshot and cumulative transfer totals |
| `/logs` | One classic `{type,payload}` message per frame |
| `/traffic` | Current upload/download rates and optional totals |
| `/memory` | Current in-use memory |

Every stream has independent cancellation, bounded backpressure, retry state, and reconnect delay. The application requests `debug` so its local severity filter can expose Debug+ through Error+ views. The transport also supports `trace` and `silent` as capability-gated extensions for callers that explicitly request them.

Connection counter deltas are normalized by their measured sample interval before presentation. Connection and traffic cumulative totals remain available independently, so a failed traffic stream can fall back to totals from the still-live connection stream.

## Capability rules

- A successful optional call or an identifying response field marks support.
- A WebSocket `400 Bad Request` for an optional stream parameter records distinct bad-request evidence.
- `405 Method Not Allowed` is strong unsupported evidence.
- `404 Not Found` only marks a static endpoint unsupported; it is not enough for a dynamic provider, rule, proxy, or connection identifier.
- `401/403` is an authentication state, not a capability result.
- Timeouts, network failures, and 5xx responses leave support unknown.
- Version and core-name detection are presentation hints only.

Wire DTOs tolerate unknown JSON fields. Required top-level payloads and required fields inside proxy, provider, rule, Smart, Honk, log, configuration, and connection entries reject missing, null, or blank values before a response can establish capability support. Required numeric fields preserve a real zero but reject an absent property, and required collections reject null members. Domain models expose the fields currently needed by the application; other unrecognized fields remain ignored until promoted into a typed compatibility feature.

The native UI does not expose `/storage/zashboard` or dashboard upgrade. Those client methods remain available for compatibility, but native preferences use packaged application local settings and application updates belong to the Windows package distribution channel.
