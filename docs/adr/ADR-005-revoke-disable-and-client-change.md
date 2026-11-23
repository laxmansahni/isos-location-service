# ADR-005: Revoke, client disable, OS permission loss and client change

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-06 |
| **Requirements** | FR-05, FR-11, FR-14, FR-15; conflict C7 |

## Context

Review questions:

- What happens when a **client disables** the feature?
- What happens to data **already collected** when the user opts out (and what if they opt in again)?
- What happens when the phone's **location permission is revoked or turned off**?
- How does cache invalidation work when a **user changes client**?

The PRD says: on revoke, capture stops immediately and points still on the phone are deleted; points already stored stay until the retention date unless Legal requires deletion now. That is PRD open question 1.

## Decision drivers

- No sharing without consent; consent and OS permission are separate things.
- Data collected under a consent that has ended should not stay visible.
- Behaviour must be testable and the same for every path that ends sharing.
- A stale cache must never expose data to the wrong tenant.

## Decision

### 1. One rule for every way sharing ends

Every end-of-sharing event sets a **visibility fence**. Queries (latest, history, replay) return only points captured **after** the fence. Fenced data is never shown again, even if sharing resumes.

| Event | Device | Server | Stored data |
|---|---|---|---|
| **User revokes consent** | Stop capture now; delete the local buffer | Consent `REVOKED`; ingestion returns `403 CONSENT_REQUIRED`; latest-position entry deleted; fence set; `policy.changed` published | Proposed default: **deleted at the next daily purge run (within 24 h)**, hidden immediately. Alternative A: keep hidden until retention. Needs Legal (open item) |
| **Client disables the feature** | Stops on next policy fetch, push or when the sharing lease ends (a device offline for up to 48 h keeps buffering until it reconnects; points are rejected on arrival) | Client `sharing_enabled=false`; ingestion returns `403 SHARING_NOT_ALLOWED`; latest entries for the client deleted; fence set for the client | Same as revoke. User consent records are **kept** so re-enabling does not need a new opt-in, but pre-fence data is not restored |
| **Logout on a device** | Stop capture; delete the device buffer | Device session ended | Kept; other devices unaffected (ADR-008) |
| **Membership ends or user changes client** | Re-fetch policy for the new client | Treated as revoke for the old client; new consent required for the new client | As revoke, under the old client's rules |
| **Erasure request** | – | GDPR workflow fences, purges every copy and records evidence | Deleted, including queue, bus, Confidence and backups per the workflow |

### 2. Opt-in again

A new consent record and sharing session start. Data from before the fence is neither shown nor restored.

### 3. Phone location permission revoked or turned off

Consent stays `GRANTED`: it is the user's choice, separate from the OS permission.

- The tracker listens for authorisation changes (`authorizationChange` in the native module; `getProviderState` for status). On loss of the required permission, location services off, or precise location off, the device stops capture and records the state.
- It reports the state to the server (device registry field `osLocationServices` already exists in device info) at the next foreground or wake, and the server marks the device session `SUSPENDED_OS_PERMISSION`.
- The map shows the last known point with its age (not removed), labelled only as "not sharing", without exposing why.
- When the permission returns, capture resumes automatically if policy still allows. No new disclosure is shown unless the disclosure version changed.
- A traveller who only grants "While Using" can share in the foreground only; the app treats background sharing as unavailable. The current code accepts both Always and While Using, so the new module must make the background requirement explicit.
- If the app is killed and cannot report, staleness is detected from the missing heartbeat.

### 4. Client change and cache invalidation

- Policy and consent are keyed by `(user, client)`. The ingestion policy cache key includes `clientId` and `policyVersion`; the client comes from the token claim.
- On change, the Policy Service evicts the entry on `user.client.changed` (event from IAM/membership; **assumption**, confirm availability). Without the event, a token that carries a different `clientId` is a cache miss, so a stale entry can be used at most until the next token.
- The latest-position key `client:user` for the old client is deleted. The map query also re-checks population membership at read time, so an old entry cannot be shown to the old client's managers after the move.
- The device clears its cached policy and buffer for the old client and re-fetches on login or token refresh. The policy response includes `clientId` and an `etag`.

## Options considered

| Topic | Options | Chosen |
|---|---|---|
| Stored data after revoke | Keep to retention (PRD) · hide and delete at next purge · delete synchronously | Hide immediately, delete at next purge, pending Legal |
| Client disable and consent | Clear consents · keep consents | Keep consents, apply fence |
| OS permission loss | Revoke consent · suspend session | Suspend session |

## Consequences

- Queries need the fence check; the purge job needs an "expedited" path for fenced data.
- Product copy: the traveller is told that data is removed after opting out (if Option "delete" is chosen).
- Devices offline for up to 48 h may upload stale points after a disable; the server drops them.

## Open items

- Legal: delete on revoke/disable, or keep until retention (PRD open question 1).
- Product: the sharing lease length that bounds a remote disable for an offline phone (PRD open question 3).
- IAM: availability of a client-change event.
- Location Confidence must purge live points per person on request (its PRD requires per-person GDPR purge, requirements L4) and apply the fence on revoke, disable and client change. The hook and timing need agreeing with that team.