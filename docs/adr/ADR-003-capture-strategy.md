# ADR-003: One low-power capture profile with a minimum spacing; no high-frequency mode

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-06 |
| **Requirements** | FR-01; NFR-07; conflict C3 |

## Context

The PRD (via HLD v0.2) puts **continuous or high-frequency GPS tracking, including a faster rate during an advisory** out of scope. SUB-5675 does not say that; it asks to "define significant change (distance/time thresholds)" and assumes the apps support background significant-change location APIs. The 150 m / 2 min floor below is from HLD v0.2: a point is sent only after at least 150 m of movement and not again within 2 minutes, with iOS significant-change often coarser than 150 m.

The base proposal contradicted this: it assumed a 500 m / maximum one per minute distance filter and, in its capture strategy, a **high-frequency profile during an AEC window**, speed tiers and activity-based moving/stationary profiles.

What ships today (`apps/mobile/lib/check-in/constants.ts`): `distanceFilter: 500`, `desiredAccuracy: High`, `useSignificantChangesOnly: false`, `locationAuthorizationRequest: "Always"`, `heartbeatInterval: 60`. That is a continuous-GPS configuration for the LBA/AEC feed.

Platform fact (to verify on devices): on iOS a `distanceFilter` on standard location updates keeps location hardware running in the background, so a 150 m filter behaves like continuous tracking for battery. The iOS significant-change service is low power but coarse (typically around 500 m and a few minutes). On Android the fused provider supports a smallest-displacement and interval setting with balanced power.

## Decision drivers

- Respect the PRD scope exclusion.
- Battery and OS review risk (continuous background location draws attention).
- Keep the contract simple and server-driven only where needed.

## Decision

1. **The 150 m / 2 min values are a minimum spacing between sent points, not a sampling target.** They bound the send rate. They do not request GPS at that rate.
2. **iOS:** significant-change capture. We accept points coarser than 150 m and do not start continuous GPS to reach 150 m.
3. **Android:** balanced-power fused location with smallest displacement 150 m and minimum interval 2 min.
4. **No high-frequency profile**, no speed tiers, no AEC capture profile. The base proposal's section 7.5 tiers are removed. During an event window (ADR-004) sharing is **enabled**, the rate is unchanged.
5. The server enforces the floor: points closer than the floor to the previous accepted point are dropped as near-duplicates (not an error to the device).
6. Heartbeat (a point while stationary) is a single configured interval. Candidates: 12 h (HLD v0.2) or 15 min (base). **Location Confidence treats location older than 30 minutes as not fresh** (requirements L1, C11), so 12 h cannot keep a stationary traveller's score fresh on its own. Preferred: the shortest heartbeat the OS allows, combined with a validity rule in Confidence (ADR-006). It also sets how stale a stationary traveller can look on the map, so Product sets it together with the map freshness target (open item).
7. Batching: the device buffers encrypted points for up to 48 h and uploads at most 100 per request, keeping the device capture time.
8. When LBA or AEC tracking is also enabled on the same device, there is **one** native tracker. Its configuration must satisfy the stricter consumer (today's 500 m / High). Live sharing then receives points at that rate, still subject to the floor.

## Consequences

- Fewer points and lower battery impact; replay is sparse, especially on iOS. Product must accept coarse trails.
- The capacity assumption drops from 1 per 5 min average to around the floor-bound rate; validate in pilot.
- Removing the AEC high-frequency profile removes the need for capture-profile delivery in the policy response beyond `allowedNow`, `validUntil` and the heartbeat.
- A mobile feasibility spike is needed for the iOS heartbeat (a timer cannot run in the background; only OS wake-ups on movement or visits are reliable).

## Open items

- Heartbeat interval and the "sharing now" freshness window (Product).
- Define "high-frequency" in the PRD as a number so the exclusion can be tested.
- Measure battery on one current iOS and one current Android device before GA (PRD).