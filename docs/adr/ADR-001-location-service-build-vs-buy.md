# ADR-001: Build an in-house Location Service (vs Vismo, Amazon Location, other)

| | |
|---|---|
| **Status** | Accepted (Architecture agreed to build in-house in HLD v0.2; repository created and implementation started, 2026-11-19). The vendor due-diligence items under "Revisit when" still apply |
| **Date** | 2026-11-06 |
| **Requirements** | FR-01, FR-05, FR-07 to FR-09, FR-11, FR-13, FR-15; NFR-05, NFR-06 |

## Context

The PRD asks for a consent-bound, auditable live-location capability whose data feeds Location Confidence and is shown to managers. The PRD also says not to send these points to Vismo or Hasura Cloud and not to store them in the check-in tables.

What the current service does, from the mobile code (`packages/feature-vismo`, `apps/mobile/lib/check-in`):

| Aspect | Current (Vismo `current/locations`) |
|---|---|
| Purpose | Feeds Location Based Alerts (LBA) and AEC; tracking runs when `aecenabled \|\| locationBasedAlerts` |
| Payload | latitude, longitude, timestamp, accuracy, deviceTime, LBA flag |
| Identity | Device headers only (`X-Device-Identifier`, `X-Device-Token`, push token, platform). No user id, client id or point id |
| Missing data | speed, course, point id (needed for idempotency and the history table) |
| Consumers | Vismo's own alerting. Not Location Confidence, not Quantum, not Tracker |
| Manager read model | None from our evidence (no map, history or replay API) |
| Consent, retention, purge, audit | Not controlled by us; vendor capability **not verified** |

## Decision drivers

1. Consent and effective-policy enforcement we control, server side.
2. Per-client retention with auditable purge through the existing GDPR workflow.
3. Tenant isolation and authorisation by function code and population scope.
4. Data residency per client (ADR-007).
5. A manager read model (latest, history, replay merged with check-ins and AECs).
6. Events to Location Confidence.
7. Delivery time, run cost, operational burden and exit cost.

## Options

| Option | What it is |
|---|---|
| A. In-house Location Service | New service on our platform: API, relational history (PostgreSQL proposed, ADR-010), latest-position store, event bus, purge job |
| B. Continue / extend Vismo | Keep posting to Vismo and ask the vendor for consent, history, read APIs and events |
| C. Amazon Location Service (Trackers) | AWS managed device tracking: devices post positions to a Tracker, with position history and geofence events |
| D. Location SaaS (for example Radar, HERE Tracking) | Vendor SDK plus backend for tracking, trips and geofences |
| E. Open-source tracking server (for example Traccar) self-hosted | Device-tracking server we operate |
| F. Cloud primitives (API Gateway/Lambda or containers, Aurora PostgreSQL, queue, bus) | The build substrate for A; not an alternative to A |

Not considered further: Google Fleet Engine and Azure Maps. They target ride, delivery and mapping scenarios rather than device-position history with consent and tenant policy. **[Verify]**

## Comparison

Ratings: **Strong** = meets the driver natively. **Partial** = meets it with work around it. **Weak** = does not. **Unknown** = vendor evidence needed. Statements about vendors are **assumptions to validate in due diligence**, not confirmed facts.

| Driver | A. In-house | B. Vismo | C. Amazon Location | D. Location SaaS | E. Open-source |
|---|---|---|---|---|---|
| Consent and policy enforcement | Strong (our rules, server side) | Unknown; today no user or consent in payload | Weak: no consent or tenant policy; we still build policy in front | Partial: consent/privacy features exist, not our policy model | Weak: build around it |
| Per-client retention and GDPR purge evidence | Strong (partition drop plus job, integrates with our GDPR workflow) | Unknown | Partial: position history has a service-defined retention and delete APIs; per-client rules and evidence need our code | Partial: vendor retention settings, evidence via vendor | Partial: we operate the store |
| Tenant isolation and function-code authorisation | Strong | Unknown | Weak: devices, not tenants or populations; we build authorisation | Partial | Weak |
| Data residency per client | Strong (we choose region) | Unknown | Partial: AWS region per Tracker; multi-region operation is ours | Unknown: vendor regions limited | Strong |
| Manager read model (latest, history, replay merge) | Strong | Unknown; none seen | Partial: reads by device id; population and merge logic ours | Partial | Weak |
| Events to Location Confidence | Strong (outbox to our bus) | Unknown | Partial: EventBridge events exist; our schema and delivery guarantees still needed | Partial: webhooks | Weak |
| Delivery time | Weak (largest build) | Strong initially for ingestion only; the missing features are the long pole | Partial | Partial | Partial |
| Run cost | Our ops and infrastructure; no per-point fee | Licence/usage fees **[verify]** | Per-update and per-query pricing **[verify at the 29 M points/day planning assumption]** | Per-MAU or per-event pricing **[verify]** | Ops cost |
| Exit cost and lock-in | Low | High (data and contract) | Medium | High | Low |
| Mobile integration | We own the native module contract | Existing endpoint | New client calls or a proxy in front | Vendor SDK in the app | Our module |
| Meets PRD "not to Vismo / Hasura Cloud / check-in tables" | Yes | **No** | Yes | Yes | Yes |

## Decision

**Option A.** Build a company-owned Location Service. Use cloud primitives (F) as the substrate.

- B is excluded by the PRD for this feature's points and does not provide the missing read and consent capabilities by itself.
- C stores positions but leaves consent, tenant policy, population authorisation, purge evidence, audit and the replay merge for us to build, so most of A remains while adding a second data store and per-update cost. It could be reconsidered later for geofencing, which the PRD excludes (no new alerting engine).
- D and E either add a vendor data processor or add an operational surface without covering the policy and read model.

## Consequences

- **Positive:** control of the data path, consent, retention, residency and audit; shaped events for Location Confidence; no per-point vendor fees; the payload contract is ours (point id, speed, course).
- **Negative:** upfront build, on-call ownership, security and privacy reviews, load testing, and a migration plan for the device tracker (ADR-006 covers LBA continuity).
- **Reversibility:** open API and events keep an exit to a vendor possible for the storage layer.

## Revisit when

Vendor due diligence shows equivalent consent, per-tenant retention and deletion evidence, residency and read APIs at lower total cost or materially lower delivery risk. Evidence required per vendor: data-processing terms and sub-processors, data locations, per-tenant retention/deletion guarantees, API and event integration, service levels, roadmap and total cost.

## Open items

- Accountable service owner and delivery estimate.
- Platform choice for runtime, cache and bus (HLD open question Q9). Storage engine: ADR-010.