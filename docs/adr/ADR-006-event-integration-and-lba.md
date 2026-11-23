# ADR-006: Integration with Location Confidence, Impact and location-based alerting

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-06 |
| **Requirements** | FR-13; PRD scope exclusions |

## Context

PRD: the Location Service publishes **one event per accepted point**. **Location Confidence** consumes it and decides the place; **Impact** assesses it; **location-based alerting** notifies only where the client already has it. This service does not decide who is inside an alert and adds no alert engine.

The base proposal said the Impact engine would consume the events and that alerting would subscribe too. That is replaced by this ADR.

**Difference from SUB-5675.** The story asks to "feed live location into the existing impact engine as a higher-priority signal than itinerary/address", assumes that engine "can accept a new location signal type/source", and lists "alerting improvements that leverage live location data" in scope. HLD v0.2 routes through Location Confidence and adds no alerting logic. This ADR follows the HLD, but it is a change from the story (requirements C8 and C9) that the Impact, Location Confidence and alerting owners must agree.

Facts from the code that affect the decision:

- Location Based Alerts today are driven by the Vismo stream. The device flag `locationBasedAlerts` turns tracking on, the same tracker posts to Vismo `current/locations`, and the push token travels in the post headers.
- The PRD says new builds do not run two trackers and this feature's points do not go to Vismo.

If the new native module simply stops posting to Vismo, **LBA stops receiving location**.

## Decision drivers

- No new alerting engine and no geospatial logic in the Location Service.
- LBA must not silently break.
- Purpose limitation: the same GPS fix must not be reused for a second purpose without a legal basis.

## Decision

### 1. Responsibility boundary

| Concern | Location Service | Location Confidence | Impact | Location-based alerting |
|---|---|---|---|---|
| Consent, policy, history, retention, manager visibility | **Owns** | – | – | – |
| Raw live points | **Owns** | Receives as evidence | – | – |
| Decide where a person is (merge itinerary, check-in, profile, live share) | – | **Owns** | – | – |
| Assess effects of a place | – | – | **Owns** | – |
| Decide who is inside an alert and notify | – | – | – | **Owns (unchanged)** |

Source check against the [Location Confidence & Impact PRD](https://internationalsos.atlassian.net/wiki/spaces/PR/pages/6423838765):

- Location Confidence produces a 0 to 100 % confidence score of where people and assets are **now**, and notifies consumers of changes within 60 s. It keeps a "signal ledger" of location history and serves queries for current, past and future locations, summaries and geo-spatial counts, under RBAC, ABAC and segmentation. Its dependencies include Kafka and an Aurora-hosted store (UNICORN-49, UNICORN-75).
- Its scope is "NOW"; **realtime location is out of scope and "will be developed and added later"**. Live-share intake is therefore not covered by that PRD.
- The Location Service does not compute confidence, summaries or geo-spatial counts, and does not store Confidence's decisions. Confidence does not own consent, per-client sharing policy, the manager history or replay, or the access audit for live location.

Where they overlap: both keep live points for a while (our history and its ledger), and both answer "who can I see". Retention, erasure and visibility rules must be the same on both sides (ADR-002, ADR-005, requirements C13 and L7).

### 2. Event contract (HLD level)

- `location.shared.v1`, one per accepted point, via transactional outbox. It carries user, client, point id, capture time, coordinates, accuracy, device id, and quality flags such as `mockLocation` and `impossibleSpeed`. Full schema in LLD 6.
- `location.policy.changed.v1` and `location.session.ended.v1` tell Confidence when live evidence has stopped, so it does not wait for a timeout alone.
- Flagged points are published **with the flag** so Confidence can discount them.
- Ordering is per user; consumers are idempotent on the point id.

### 3. Confidence consumer

Live-share intake in Location Confidence is **new work with no owner yet**, and the Location Confidence PRD puts realtime location out of scope for now (requirements C12). Without it nothing downstream of the events happens. Required from that team: owner, story and date; the signal contract (its ingestion story LIP-13 and linked LLD/data structure define it; our event follows that contract rather than the reverse); the erasure hook for its ledger; the region of the consumer (ADR-007); and the validity rule below.

**Freshness and validity.** Confidence considers location older than **30 minutes** not fresh (requirements L1). The earlier candidate of 13 hours after the last point is withdrawn. A traveller who stops moving sends no significant-change point, so unless the heartbeat is short, live evidence would drop out after 30 minutes. Options:

| Option | Effect |
|---|---|
| 1. Heartbeat at most about 15 to 30 min | Keeps the score fresh; costs battery and may not be possible on iOS while stationary (ADR-003) |
| 2. **Validity rule in Confidence**: a live point stays valid until the next point, `location.session.ended.v1`, or a maximum age | No extra battery; matches its existing rule for "flight arrives, no new data" which needs a business rule to extend validity; Confidence must implement it |
| 3. Both: a longer heartbeat plus the validity rule | Balance; the interval and maximum age are agreed by Product and the Confidence team |

Proposed: option 3, decided with the Confidence team. The session-ended event is what lets Confidence stop trusting a stationary point at the right moment.

**Transport.** Confidence depends on Kafka (UNICORN-49). Use the same bus for our events unless Platform says otherwise (HLD Q9).

### 4. LBA continuity and retiring Vismo

**Confirmed by the product owner (via the project owner, 2026-11-21):** Vismo is retired over time, once the native service does what Vismo does today. Until then both run side by side.

- **Phase 1 (now):** the LBA/AEC Vismo stream stays **unchanged** for users who have LBA or AEC enabled. The Location Service is an additional stream. One native tracker produces points and two sinks send them. Points go to Vismo only for the LBA/AEC purpose, never because of live sharing.
- **Phase 2 (retirement):** the Vismo post is switched off for a user or app version once the new path covers everything Vismo does for them. The AEC and LBA points then reach the Location Service only, with `shared=false` unless live sharing is on (the "not shared" story).
- **What "does what Vismo does" has to include.** The current post (`current/locations`) carries the device push token and the LBA flag, and Vismo uses them to match locations to alerts and send push notifications. The Location Service stores points and publishes events; it adds no alerting (scope exclusion). So retirement depends on the downstream consumers (Location Confidence, Impact, location-based alerting) taking over alert matching and the push, not only on our endpoint. That is the real exit criterion and needs an owner.
- **Cut-over rule (proposed):** switch per app version through a remote flag, not all at once. Keep Vismo posting until alert delivery from the new path has been compared against Vismo for a pilot group. Older app versions keep posting to Vismo until they age out.
- **Parity data the new path would need:** device push token and platform (so the alert push can be addressed), the LBA-enabled flag, and device identity. These are not in the points contract today. They belong to a device-registration call, not to every point, and are open.

## Options considered

| Option | Result |
|---|---|
| A. Stop posting to Vismo from new builds | Breaks LBA and AEC feed. Rejected until the new path covers what Vismo does (see section 4) |
| B. Dual post, both purposes, same consent | Violates the PRD wording and purpose limitation. Rejected |
| C. **One tracker, two purpose-bound sinks (interim)** | Keeps LBA; duplicates uploads; needs sign-off |
| D. Move LBA onto the event stream now | Cleanest end state; depends on Confidence and alerting changes not planned |

## Consequences

- Two uploads for users in both features; the tracker's config follows the stricter consumer (ADR-003).
- The metric "alerts split by location source" needs an owner in alerting; the Location Service only supplies events.
- Vismo retirement is a separate piece of work with its own plan, gated on the exit criterion in section 4. Until then users with AEC or LBA upload twice.

## Open items

- Owner, story and date for Confidence live-share intake; agree the signal contract and the validity rule with the Confidence team (read their HLD, LLD and data-structure pages).
- Whether Confidence's ledger copy of live points follows our retention and fence (Privacy).
- **Confidence summary story (assigned to the Confidence team, 2026-11-21).** "Location Confidence Summary For an individual location" returns counts of people in a location by risk rating, with a parent location and a winner-only option for conflicting locations. It is not built here: this service has no location bounds, risk ratings or conflict resolution. It depends on us only through the event stream. Open for that team: whether the counts may include `shared=false` points (Privacy), and the 30-minute freshness against our heartbeat (requirements C11).
- Product/Legal confirmation of option C.
- **Built (2026-11-21):** the `shared` flag, the `latest_location` table and the "not shared" rule (history only for shared points; latest position for both; newest capture time wins; `Superseded` status). Not built: the SQS outbox and publisher, and server-side verification of the AEC and LBA flags (the service currently trusts `shared` from the app).
- **Retention of "not shared" latest locations.** Proposed default: the same as live location (14 days or the client's retention, ADR-002). Only one row per person is kept and a newer point overwrites it, so the retention date is a cap on how old the row can get, enforced by the same purge job through `retention_expires_at`. Turning AEC or LBA off deletes the row promptly under the ADR-005 fence rule. Legal and Privacy must confirm, because these users did not opt in to live sharing and a shorter cap may be preferred (a position older than a day has little value for alerts).
- **How the service learns that AEC or LBA is on.** The app already reads and writes these flags through the unified API: `GET unifiedappsapi/deviceinfo?partyId=<partyId>` returns them, and `POST unifiedappsapi/deviceinfo` updates them (fields `userSettings.locationBasedAlerts`, `accountAECEnabled` for the account and `aecEnabled` for the user, plus `deviceInformation` and the push token). Options: (a) the app sends the flags with each batch, which is cheap but untrusted; (b) the Location Service reads `deviceinfo` for the party and caches it briefly, which is verified but adds a dependency on the unified API, service-to-service authentication and a per-user cache; (c) the app sends `shared` (as now) and the server checks (b) only to accept `shared=false` points. Proposed: (c). Live-sharing consent and mode are ours and are not in `deviceinfo`.
- **Meaning of the AEC flags (unconfirmed, to check with the unified API owners).** Working reading: `accountAECEnabled` is the account-level entitlement and `aecEnabled` is the user's own toggle. The app starts tracking on `aecenabled || locationBasedAlerts` only (`apps/mobile/app/landing.tsx`) and ignores `accountAECEnabled`. Proposed rule for "AEC is on": `aecEnabled` and `accountAECEnabled` both true, which is stricter than the app today. The GET response is read by the app as lowercase `aecenabled`; the POST body uses `aecEnabled`.
- **Device registration for the Vismo retirement parity data** (push token, LBA flag, device identity) already exists in `POST unifiedappsapi/deviceinfo`. The new path can reuse it instead of a new call, so the retirement criterion is about alert matching and push delivery, not about collecting new data.
- Whether alerting can attribute a notification to a live point (for the success metric).