# ADR-007: Store location in the client's data region

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-06 |
| **Requirements** | NFR-06 |

## Context

Review question: is data stored under the **user's** region or the **client's** region? The PRD requires a point to be accepted in one approved region and not copied elsewhere for a map in a second region.

The platform already resolves a region **per client organisation**: `getRegion(sfId)` calls the region API with the Salesforce id, and the mobile config (`AppMobileConfig.load(region)`) selects endpoints per region (an EU/FR set and a default set). The device already carries the Salesforce account id in user settings.

## Decision drivers

- Contractual data residency is normally agreed per client.
- Managers of one client can sit in different countries from the traveller.
- One tenant should live in one place so that purge and erasure are provable.

## Options

| Option | Notes |
|---|---|
| 1. User's physical region (where the phone is) | Changes while travelling; splits one client's data across regions; unworkable for retention and purge |
| 2. User's home/residence region | Matches data-subject law; a manager in another region still has to read it; splits a client's data |
| 3. **Client's data region** | Matches the existing region lookup; one tenant, one region |

## Decision

**Option 3.**

- The data region is the client's, resolved through the existing client-to-region lookup. All of the client's travellers' points, consent, policy, audit and events live there.
- The mobile app takes the Location Service base URL from the same region config used for other APIs.
- Managers' reads are served from the client's region; the browser calls that region. Nothing is replicated to another region for map reads.
- The **user's** region is kept on the consent record and drives the consent requirement profile and any maximum retention that law imposes (LLD 2.1). It affects rules, not where data is stored, unless Legal or the client contract says otherwise.
- Consumers of the events (Location Confidence, Impact, GDPR workflow) must be in the same region or approved by Security and Legal. The Location Confidence PRD says it "must abide by data sovereignty for each client" (requirements L6), which supports this decision, but how its regions are deployed is not stated on that page and must be confirmed. If Location Confidence is central, the event transport would copy coordinates across regions: this must be answered before design is final.

## Consequences

- One deployment (or deployment cell) per data region; routing by client at the gateway.
- Consent record and disclosure per region.

## Open items

- Which regions exist at launch and their approved clouds (Platform).
- Residency of Location Confidence and Impact (Architecture).
- A client whose travellers live under a stricter law than the client's region (Legal).