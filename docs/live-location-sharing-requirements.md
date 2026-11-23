# Live Location Sharing – Problem Statement and Requirements Baseline
| | |
|---|---|
| **Purpose** | Single source for the problem and the requirements that the architecture answers. Read this before the HLD. |
| **Status** | Draft for review |
| **Companion documents** | [HLD](./live-location-sharing-backend-architecture.md) · [LLD](./live-location-sharing-lld.md) · [ADR index](./adr/README.md) · [Review responses](./live-location-sharing-review-responses.md) |

## 1. Source documents (the requirement set this solution is traced to)

| Ref | Document | Location | Notes |
|---|---|---|---|
| PRD | Subscriber live location PRD, tracked as epic [SX-76](https://internationalsos.atlassian.net/browse/SX-76) | [SX-76](https://internationalsos.atlassian.net/browse/SX-76) | The epic holds three stories: [SUB-5354](https://internationalsos.atlassian.net/browse/SUB-5354), [SUB-5652](https://internationalsos.atlassian.net/browse/SUB-5652) and [SUB-5675](https://internationalsos.atlassian.net/browse/SUB-5675). PRD statements that come only from Architecture's HLD v0.2 (page 1, "Product ask") are marked *PRD (via HLD v0.2)* and still need verifying |
| Story (read) | **SUB-5675 "Non-App – Live Location Sharing"**: problem statement, scope, personas, assumptions and six acceptance-criteria groups | [SUB-5675](https://internationalsos.atlassian.net/browse/SUB-5675) | Content supplied by the product team and used as the baseline. Its acceptance criteria reference SUB-5362, 5363, 5364, 5365, 5366, 5371 and 5411 |
| Stories (not read) | **SUB-5354** and **SUB-5652** | Jira | Not provided. SUB-5675 is titled "Non-App", so these are likely the app-side stories (consent, capture). Capture, consent and OS-permission requirements (FR-01, FR-03, FR-05, ADR-003 to ADR-005) must be checked against them. This may also explain the "two spellings" of the manager permission code the PRD mentions, since SUB-5675 spells it consistently as `MGRLOCHIS` |
| Tickets | SUB-5362, 5363, 5364, 5365, 5366, 5371, 5411 | Jira | Sub-tasks quoted in SUB-5675's acceptance criteria |
| HLD v0.2 | `PH-Live-location-sharing-subscriber-HLD-051126-053605.pdf` | Architecture team response, 23 Oct 2026 | Reviewed in this repo's history; see section 5 conflicts |
| Proposal | Live Location Sharing Backend Architecture Proposal | [Confluence](https://internationalsos.atlassian.net/wiki/spaces/H3SProd/pages/6905462863/Live+Location+Sharing+Backend+Architecture+Proposal) | Published copy of the HLD in this folder |
| LC PRD | **Location Confidence & Impact** PRD (consumer of live location; relates to CEM-211, CEM-253, LIP-13) | [Confluence](https://internationalsos.atlassian.net/wiki/spaces/PR/pages/6423838765)| **This is the consumer's PRD, not the PRD for live location sharing.** It never mentions live location sharing or SX-76. It sets constraints the live-sharing events must meet (section 3.4). Its linked HLD, LLD, data-structure and ADR pages were not provided |

## 2. Problem statement

**Who is affected.** Assistance and security managers at client organisations who use Quantum (and Tracker) to look after travellers, and the travellers themselves.

**The problem.** *(SUB-5675)* Managers need to control what options a user has and what they can enable for location sharing, and to see the data that has been shared. Today a manager can see a traveller's itinerary and stored address but cannot see where that person is now, unless the person has just checked in. Impact calculation relies on itinerary data or last-known addresses that may be stale, and the manager has no way to confirm them.

**Personas** *(SUB-5675)*: the traveller (shares, controls consent, receives alerts), the manager (views location in Quantum/Tracker to assess risk and respond), and impact/intelligence services (consume live location as a signal next to itinerary and news/risk data).

**Commercial drivers** *(SUB-5675)*: reduce DNR (did not renew), win new business, extend existing accounts, increase population/seat coverage, delight users.

**What exists today.** The mobile app already tracks location in the background and posts it to a third-party vendor endpoint (Vismo `current/locations`). From the code:

- Tracking runs only for users with Location Based Alerts (LBA) or Automated Emergency Check-in (AEC) enabled.
- The post carries latitude, longitude, timestamp, accuracy and device headers. It has **no user id, client id, point id, speed or course**.
- That stream feeds LBA and AEC. It does **not** feed Location Confidence, the manager map, or any history.
- There is no consent record, per-client retention, manager access audit or purge evidence that we control.

**Why now.** Managers need a consent-bound, auditable view of live location, and Location Confidence needs live location as fresher evidence than itinerary or address.

**What a solution must achieve**

1. Travellers opt in explicitly, can opt out at any time, and nothing is shared without that consent and the OS location permission.
2. Clients choose whether and how sharing is allowed (sharing types, retention).
3. Authorised managers see the people in their scope who are sharing now, with name and time, plus history and replay.
4. Location Confidence receives live location as evidence; Impact and location-based alerting keep working where they are.
5. Location is retained for a defined period and deleted through the existing GDPR workflow with evidence.

**Success measures**

| Measure | Source |
|---|---|
| Increase in the number of alerts sent to users (needs an alert split by location source) | SUB-5675 product metric |
| Increase in RFPs won that list live location as a requirement (CRM/BI, not runtime) | SUB-5675 product metric |
| Active sharers, consent rate, points per day, sharing-type usage | Location Service events (proposed) |
| Enable / disable / share counts by client, with no coordinates | PRD (via HLD v0.2) |

Targets are not set in SUB-5675; Product to set them.

**In scope** *(SUB-5675)*

- Manager-facing view of shared live location in Quantum/Tracker.
- Alerting improvements that leverage live location data.
- Historical location trail and playback, beyond what impact calculation needs.
- Client configuration to enable or disable location sharing entirely, or enable it only for active travellers.

**Out of scope** *(SUB-5675, "initial release – confirm with team")*

- Visibility in Tracker (contradicts the in-scope list; see C6).
- Location sharing without explicit user consent.

**Out of scope** *(PRD via HLD v0.2; not in SUB-5675)*

- Continuous or high-frequency GPS, including a faster rate during an advisory.
- Points before consent and OS permission.
- A new alerting engine inside this system.
- A dispatcher session, a few-second update rate or a live socket.
- Sending these points to Vismo or Hasura, or storing them in the check-in tables.
- The RFP/deal metric (stays in CRM).

## 3. Requirements baseline

Status: **Confirmed** = consistent across sources. **Conflict** = sources disagree (section 5). **Confirm** = stated in only one source or not defined.

### 3.1 Functional

| ID | Requirement | Source | Status | Addressed in |
|---|---|---|---|---|
| FR-01 | Mobile sends location points to the Location Service in foreground and background, only when policy allows | PRD | Confirmed | HLD 5.1 · LLD 3 · ADR-003 |
| FR-02 | Client admin configures which sharing types exist and the retention | SUB-5411, PRD | Confirmed | HLD 6 · LLD 2.1 · ADR-004 |
| FR-03 | Traveller picks one primary sharing type and may set a schedule within client-enabled types | PRD | Confirm (base proposal assumed schedule is client-level) | ADR-004 |
| FR-04 | Event-triggered sharing after AEC (1 h) or a special advisory | SUB-5411 (AEC), PRD (advisory) | Conflict: AEC vs advisory | ADR-004 |
| FR-05 | Explicit opt-in, revocable at any time; capture stops immediately on revoke | PRD | Confirmed | HLD 6 · ADR-005 |
| FR-06 | Manager function code to view user history (`MGRLOCHIS`, default on the Manager role), member function code to view own history (`USRLOCHIS`, default on members), plus an admin toggle to allow or prevent users seeing their own history | SUB-5675 AC 2 and 3 (SUB-5365, 5366, 5411) | Confirmed in SUB-5675; PRD says other stories spell the manager code differently | HLD 6 · LLD 2.1 |
| FR-07 | Manager map shows latest location per user with a distinct icon separate from check-ins; selecting it shows first and last name and timestamp; a filter toggles live-location shares on/off | SUB-5675 AC 1 (SUB-5362) | Confirmed | HLD 5.3 · LLD 4.3 |
| FR-08 | History table on the profile for `USRLOCHIS` (own) or `MGRLOCHIS` (others): type, lat/long, speed and course if available, accuracy (m), timestamp; message when empty | SUB-5675 AC 4 (SUB-5371) | Confirmed | LLD 2.2, 4.4 |
| FR-09 | Replay on the profile for `USRLOCHIS` or `MGRLOCHIS`: chronological, check-ins and AECs with distinct icons, location shares as basic points; details per point; message when empty | SUB-5675 AC 5 (SUB-5364) | Confirmed | HLD 5.4 · LLD 4.4 |
| FR-10 | Query ranges: last 1 h (default), 24 h, 7 d for both history and replay (SUB-5675); PRD adds 14 d for replay | SUB-5675 AC 4 and 5, PRD (via HLD v0.2) | **Conflict** (C2) | ADR-002 |
| FR-11 | Retention: 30 days by default or the client's policy; automatic purge through existing GDPR-compliant mechanisms (SUB-5675); PRD says 14 days | SUB-5675 AC 6 (SUB-5363), PRD (via HLD v0.2) | **Conflict** (C1) | ADR-002 |
| FR-12 | Manager view in Quantum/Tracker | SUB-5675 in scope and out of scope; PRD (via HLD v0.2) says one read API for both | **Conflict** (C6, inside SUB-5675) | HLD 1.2 |
| FR-13 | Live location feeds impact calculation as a higher-priority signal than itinerary or address. SUB-5675 says "the existing impact engine"; Architecture's HLD v0.2 routes it through Location Confidence (one event per accepted point), with Impact downstream | SUB-5675 capability 4, PRD (via HLD v0.2) | **Conflict** (C8) | HLD 7 · ADR-006 |
| FR-14 | Multiple devices per user handled; logout ends that device, revoke and client disable end the person | PRD | Confirmed (rule detail in ADR-008) | ADR-008 |
| FR-15 | Erasure and expiry through the existing GDPR workflow, with purge evidence | SUB-5363, PRD | Confirmed | HLD 8 · LLD 5 |
| FR-16 | Counts of enable, disable and share by client, without coordinates | PRD | Confirmed | HLD 8 |
| FR-17 | Alerting improvements that leverage live location, and tracking of alert volume against the product metric | SUB-5675 in scope, capabilities 5 and 6 | **Conflict** (C9): PRD (via HLD v0.2) says no new alert engine and alerting unchanged | ADR-006 |
| FR-18 | Admin can enable sharing entirely, or only for active travellers; each option has a short explanation (info icon or explainer page) | SUB-5675 AC 2 and in scope | Confirmed | HLD 6 · ADR-004 |
| FR-19 | Tag RFPs and deals where live location is a stated requirement | SUB-5675 capability 6 | Confirm: PRD (via HLD v0.2) keeps this in CRM; not a runtime requirement | HLD 7 |
| FR-20 | Live location is shared "in real time" | SUB-5675 description | **Conflict** (C10): no latency target is stated, and the PRD (via HLD v0.2) excludes a few-second rate and a live socket | ADR-009 |

### 3.2 Non-functional (targets to confirm)

| ID | Concern | Target | Status |
|---|---|---|---|
| NFR-01 | Ingestion availability | 99.9 %, at-least-once and idempotent | Proposed |
| NFR-02 | Ingestion latency | p95 < 300 ms API response; event to consumers p95 < 5 s | Proposed |
| NFR-03 | Manager map query | p95 < 500 ms for a client-wide latest view | Proposed |
| NFR-04 | History query | p95 < 1 s for the longest allowed range of one user | Proposed |
| NFR-05 | Data protection | TLS 1.2+, encryption at rest, tenant isolation, coordinates out of logs, audit and analytics; privacy and retention policy for who can access location | SUB-5675, PRD |
| NFR-06 | Data residency | Stored in one approved region, not copied to another | PRD |
| NFR-07 | Battery and performance | Measured on one current iOS and one current Android device before GA; no number set | SUB-5675, PRD |
| NFR-08 | Capacity | Neither live-location source sets a TPS target. The Location Confidence PRD requires ingestion for at least 500,000 people and 4× spikes (L3); planning figures for both 100,000 and 500,000 sharers are in HLD 9 | Proposed |
| NFR-09 | Regulatory | Consent requirements for location vary by region | SUB-5675 |

### 3.3 Assumptions and dependencies (SUB-5675)

| Assumption | Where it is tested |
|---|---|
| Quantum/Tracker has, or needs, a UI surface to show live location | Delivery of the Quantum and Tracker screens is not a backend item; Tracker shipping is open (C6) |
| The impact calculation engine can accept a new location signal type/source | Needs an owner; Architecture's HLD routes it through Location Confidence instead (C8) |
| Mobile apps support background significant-change location APIs | ADR-003; iOS has them; Android uses balanced-power fused location; the story implies significant-change, so it does not itself exclude a distance threshold, which the story leaves to "define significant change (distance/time thresholds)" |

### 3.4 Constraints from the Location Confidence & Impact PRD (the consumer)

| # | Statement in the Location Confidence PRD | Effect on live location sharing |
|---|---|---|
| L1 | Location confidence only accounts for "NOW"; **how up to date is acceptable: 30 minutes** (answered 12 May 2026). Metric: at least 95 % of people and assets with more than static addresses have a score updated in the last 30 minutes | A live point that is older than 30 minutes is no longer fresh evidence. With no movement, a significant-change tracker sends nothing, so a stationary traveller drops out unless the heartbeat is at most about 30 minutes or Confidence adds a validity rule (C11) |
| L2 | "Realtime location will be developed and added later" is **out of scope**; location-history retention for the signal ledger is "match existing mechanisms (check with Privacy), Realtime TBD" | Live sharing looks like the deferred realtime source, so intake may not be planned (C12). Retention of live points in Confidence's ledger must be agreed with Privacy (C13) |
| L3 | Ingest at least 500,000 people concurrently without degradation; handle 4× traffic spikes | Much larger than the 100,000 sharers in the planning assumption (NFR-08, HLD 9) |
| L4 | All location data must be purgeable per person on request (GDPR) | Confidence's ledger needs an erasure hook for live points (ADR-005) |
| L5 | Location history retention must match existing retention mechanisms and be approved by the Privacy team | The Location Service retention (14 or 30 days) needs Privacy approval too (ADR-002) |
| L6 | Must abide by data sovereignty for each client | Consistent with storing in the client's region (ADR-007) |
| L7 | All data returned abides by RBAC, ABAC and segmentation; managers only see people they have permission to see (UNICORN-10 "ABAC & Segmentation") | Our population-scope check must use the same ABAC and segmentation model rather than a separate one (HLD 6) |
| L8 | Change events are delivered to consumers within 60 s of a score update | Sets an upper bound on how fast our event must reach Confidence for the 60 s target to be achievable (NFR-02 is 5 s) |
| L9 | Risks and dependencies on the page: Kafka setup (UNICORN-49, blocker), TimescaleDB not supported on Aurora (UNICORN-75, critical), Data Products streaming | Kafka is the event transport Confidence depends on; Aurora (PostgreSQL) is its store. Both are relevant to our bus and database choice (ADR-010, HLD Q9) |

## 4. Traceability summary

| Ticket / PRD item | Requirement IDs | HLD section | ADR |
|---|---|---|---|
| SUB-5362 map | FR-07 | 5.3 | ADR-009 |
| SUB-5363 retention | FR-11, FR-15 | 8 | ADR-002, ADR-005 |
| SUB-5364 replay | FR-09, FR-10 | 5.4 | ADR-002 |
| SUB-5365 / 5366 permissions | FR-06 | 6 | – |
| SUB-5371 history | FR-08, FR-10 | 5.4 | ADR-002 |
| SUB-5411 admin config | FR-02, FR-04, FR-18 | 6 | ADR-004 |
| SUB-5675 capability 4 (impact) | FR-13 | 7 | ADR-006 |
| SUB-5675 capabilities 5 and 6 (alerting, metrics) | FR-16, FR-17, FR-19 | 7 | ADR-006 |
| PRD capture rules | FR-01 | 5.1 | ADR-003 |
| PRD consent and revoke | FR-05 | 6 | ADR-005 |
| SUB-5354 and SUB-5652 | Not read | – | – |

## 5. Conflicts between sources (need an owner decision)

| # | Conflict | Sources | Proposed resolution | Owner |
|---|---|---|---|---|
| C1 | Default retention is 30 days vs 14 days. SUB-5675 is also internally inconsistent: 30 days retained, but only 7 days can be queried | SUB-5675 / SUB-5363 vs PRD (via HLD v0.2) | 14 days (PRD) with retention equal to the longest queryable range; amend SUB-5363. If 30 days is kept, the ranges must grow to 30 days. ADR-002 | Product |
| C2 | Replay range tops out at 7 d vs 14 d | SUB-5675 / SUB-5371, SUB-5364 vs PRD (via HLD v0.2) | Add 14 d; amend SUB-5371 and SUB-5364. ADR-002 | Product |
| C3 | The PRD (via HLD v0.2) puts high-frequency tracking out of scope and sets a 150 m / 2 min floor; SUB-5675 only asks to define "significant change" thresholds. The base proposal had a 500 m / max 1 per minute rate and an AEC high-frequency profile, which conflicts with both | PRD, SUB-5675, base proposal | Remove the high-frequency profile; single low-power capture profile. ADR-003 | Architecture, Mobile |
| C4 | AEC trigger (SUB-5675 AC 2: AEC auto-enabled for 1 hour) vs "special advisory" (PRD via HLD v0.2) as the event-triggered sharing condition | SUB-5675 / SUB-5411 vs PRD | One generic event-window mode with a trigger type; ship once Product defines the window. ADR-004 | Product |
| C5 | Schedule owned by client vs by traveller. SUB-5675 lists "Schedule" as a client-enabled mode without saying who sets the windows | SUB-5675, PRD (via HLD v0.2) | Client enables the type, traveller sets the schedule. ADR-004 | Product |
| C6 | **Inside SUB-5675:** "Manager-facing view in Quantum/Tracker" is in scope and "Visibility in Tracker" is out of scope ("confirm with team"). PRD (via HLD v0.2) has one read API for both | SUB-5675 vs itself, PRD | Backend serves both through one API; Tracker UI delivery tracked separately. HLD 1.2 | Product |
| C7 | After revoke or client disable, do stored points stay until retention? | PRD open question | Fence immediately, delete at the next purge run unless Legal requires otherwise. ADR-005 | Legal, Product |
| C8 | Live location feeds "the existing impact engine" (SUB-5675) vs Location Confidence consumes it and Impact assesses (HLD v0.2) | SUB-5675 vs PRD (via HLD v0.2) | Follow the HLD: events go to Location Confidence. Needs the Impact and Confidence owners to agree and an owner for the new intake. ADR-006 | Architecture, Impact team |
| C9 | "Alerting improvements that leverage live location" in scope (SUB-5675) vs no new alerting engine and alerting unchanged (HLD v0.2) | SUB-5675 vs PRD (via HLD v0.2) | The Location Service supplies events only; any alerting change is separate work owned by the alerting team and needs its own story | Product |
| C10 | "Share their location in real time" (SUB-5675) vs no few-second rate or live socket, polling for the map (HLD v0.2). No latency target is stated anywhere | SUB-5675 vs PRD (via HLD v0.2) | Turn "real time" into a number (for example, a new point shows on the map within N seconds) and check polling can meet it. ADR-009 | Product |
| C11 | Location Confidence treats location older than **30 minutes** as not fresh (L1). The HLD v0.2 heartbeat is 12 h and its candidate freshness is 13 h; the base proposal had a 15 min heartbeat. iOS significant-change sends nothing while stationary | LC PRD vs HLD v0.2 | Product, Confidence and Mobile agree one rule: a heartbeat at most about 15 to 30 min where the OS allows it, and/or a Confidence validity rule that a live point stays valid until the next point or the session ends (like its one-way-flight rule). ADR-003, ADR-006 | Product, Confidence team, Mobile |
| C12 | Location Confidence lists realtime location as out of scope and later (L2). Live sharing needs a Confidence intake | LC PRD vs SUB-5675 | Treat live-share intake as unplanned work: owner, story and date needed before build. ADR-006 | Confidence team, Product |
| C13 | Retention of live points would exist in two places: our history (14 or 30 d) and Confidence's signal ledger ("match existing mechanisms", Privacy) | LC PRD vs this design | Confidence's copy of live points must not outlive the Location Service retention, and must honour the fence and erasure. ADR-002, ADR-005 | Privacy, Confidence team |
