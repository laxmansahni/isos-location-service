# ADR-008: Newest allowed point across a user's sharing devices

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-06 |
| **Requirements** | FR-14 |

## Context

A user can have more than one device. Review question: how do we know which device takes priority? The PRD says logout ends that phone, revoke and client disable end the person, and the map shows the newest allowed point from phones that are still sharing.

The platform already has a device registry: device info carries `deviceID`, `platform`, `model`, `osVersion`, `partyID`, `osLocationServices` and `osPushEnabled`. The current tracker sends `X-Device-Identifier` (a unique install id).

## Decision

**No static priority. The effective location is the newest allowed point from any device with an active sharing session.**

1. Every point carries a `deviceId`. History keeps all points, tagged by device.
2. Each device has its own sharing session (`ACTIVE`, `SUSPENDED_OS_PERMISSION`, `ENDED`). Logout ends only that device.
3. **Latest position** per user is the point with the greatest `captured_at` among `ACTIVE` devices. The stored value is replaced only by a newer `captured_at`, so a buffered batch from the second device cannot overwrite a newer point.
4. Tie or near-tie: prefer better accuracy, then later `received_at`.
5. `captured_at` more than a small skew in the future is clamped to `received_at` so a wrong clock cannot pin the position.
6. The map and history show the user, not the device; the device id is not shown to managers.
7. Revoke, client disable and client change end every device of the person (ADR-005).
8. An optional cap on concurrently sharing devices (for example 3) limits battery and duplicate uploads. Default: no cap, configurable.

## Options

| Option | Notes |
|---|---|
| Primary device chosen by user | More control, more UI; stale if the primary is off |
| Phone over tablet/watch by platform | Needs device class data we do not hold today |
| **Newest point wins** | Matches the PRD, no extra UI, self-healing |

## Consequences

- Two devices in different places make the position jump between them. History shows both; this is correct, and the UI should show the age.
- Duplicate points from two devices at the same place add some volume.

## Open items

- Product: do we want a phone-preferred rule or a device cap?