# ADR-009: Manager map transport – delta polling first; own gateway later; no Hasura Cloud

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-06 |
| **Requirements** | FR-07, FR-12; NFR-03 |

## Context

Only the manager live-map layer needs updates over time. Mobile uploads are write-only batched HTTPS, and history and replay are bounded request/response. SUB-5675 says users share "in real time" but sets no latency target (requirements C10), and the PRD (via HLD v0.2) states there is **no live socket in the first release**.

## Decision drivers

- Meet the PRD: polling, no socket in the first release.
- Keep authorisation (function code plus population scope) and the access audit in our code.
- Keep location out of vendor-hosted Hasura Cloud (PRD).

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Delta polling (`GET /latest?since=`) with a shared per-client cache, ETag and `304`, jittered interval, only while the layer is visible | No new infrastructure; stateless; simple authorisation; scales by caching | Up to one interval of staleness; empty polls |
| B. WebSocket/SSE through our own Location Gateway | Push only on change; fewer empty requests | Stateful infrastructure; out of PRD scope for release 1 |
| C. GraphQL subscriptions on Hasura | Built-in subscription engine | Hasura URLs in the app are Hasura Cloud (vendor-hosted); subscriptions are database-driven; duplicates authorisation rules; no per-subscription access audit; admin secrets are bundled in `EXPO_PUBLIC_*` variables and must not be extended to location |

## Decision

**A for the first release.** B is a later option for the live-map layer only, if Product sets a freshness target polling cannot meet. C is not used.

- Polling load depends on concurrent managers viewing, not on travellers. With delta polling and a shared per-client cache, 2,000 managers at a 30 s interval give about 67 requests/s, mostly `304` or under 1 KB (vs about 10 MB/s for full-list polling). Planning figures; validate.
- The snapshot-plus-`since` endpoint is needed for first load and reconnect even if B is added.
- Replay and history stay plain request/response.

## Consequences

- A freshness target (seconds) must be agreed (open item) because polling has a floor of one interval.
- The gateway design (LLD 7) is kept for later use.

## Open items

- Required map freshness and expected concurrent managers (Product, Quantum team).
- Whether Quantum already has a GraphQL/WebSocket layer.
- Hasura admin secrets in `EXPO_PUBLIC_*` variables: review separately.