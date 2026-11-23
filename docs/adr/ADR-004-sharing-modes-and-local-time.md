# ADR-004: Sharing modes, event-window activation and local-time schedule

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-06 |
| **Requirements** | FR-02, FR-03, FR-04; conflicts C4, C5 |

## Context

Four questions came out of review:

1. Is AEC the same as the PRD's "special advisory"?
2. Who owns the schedule, and what does "local hours" mean for a traveller?
3. How are the traveller's local hours calculated?
4. AEC activation was designed around a silent push. iOS limits and delays silent pushes.

What the code shows: "special advisory" is an **alert severity** (`severityType` in the alert flow, `useProcessVismoAlertQueue`). AEC is a separate **Automated Emergency Check-in** workflow with its own device flag (`aecenabled` in the device and user settings, `accountAECEnabled` at account level). The PRD's candidate for the advisory condition is "one hour after an AEC", so the PRD is using the AEC trigger as a proxy, but it says the condition is not yet defined. They are related, not the same.

## Decision drivers

- Do not wait for Product's definition to fix the schema.
- Consent and OS permission are never bypassed.
- Time rules must be right for people who cross time zones.
- Activation must not depend on a delivery channel that the OS does not guarantee.

## Decision

### 1. Modes

| Mode | Meaning | Set by |
|---|---|---|
| `ALWAYS` | Allowed whenever consent and OS permission hold | Client enables the type; traveller chooses it |
| `SCHEDULE` | Allowed inside the traveller's weekly windows | Client enables the type; **traveller** sets the windows (PRD) |
| `ACTIVE_TRAVEL` | Allowed while the traveller has an active trip | Client enables; derived from Trips |
| `EVENT_WINDOW` | Allowed for a window after a trigger | Client enables; started by an event |

- A client enables a set of types. The traveller picks **one primary type** (PRD). `EVENT_WINDOW` is **additive**: it can switch sharing on even when the primary type is closed, but only if consent is already granted.
- `EVENT_WINDOW` has a `trigger_type` (`AEC` | `SPECIAL_ADVISORY`) and a `window_minutes` (default 60). AEC is the first trigger. "Special advisory" is enabled when Product defines it (PRD open question 2). No schema change is needed to switch.
- The window changes **whether** sharing is allowed, not **how often** points are sent (ADR-003).

### 2. Local hours

Schedule windows are weekly `(day_of_week, start, end)` in the **traveller's local time**.

Resolution order for the zone:

1. The device's IANA time zone, sent with each policy request and with each point batch as `tz` plus `utcOffsetMinutes` for each point.
2. The traveller's profile home zone.
3. The client default zone.

Rules:

- The device evaluates the schedule itself so it works offline; the server re-evaluates at ingestion using the capture time and the point's offset.
- A window may cross midnight; evaluation uses the IANA rules, so daylight-saving changes are handled.
- A point whose offset disagrees with the batch zone, or whose capture time is far from receive time, is flagged for review and not blocked for that reason alone.
- A traveller moving between zones sees the window follow them. If the PRD means "the client's office hours", that is the client zone and the resolution order inverts. Product confirms (open item).

### 3. Activation reliability (iOS and Android)

Silent push is **best effort, not the mechanism**. Platform behaviour to verify on test devices: iOS may throttle or drop silent pushes (not delivered after a user force-quit, with Background App Refresh off or in Low Power Mode); Android high-priority data messages can be delayed by Doze and app standby buckets.

Therefore:

- The device re-evaluates policy at: app foreground, login and token refresh, every OS location wake-up, the heartbeat, receipt of the existing user-facing AEC notification, and `nextEvaluationAt`.
- Silent push is an accelerator on top of these.
- The **server accepts any point whose capture time falls inside an active window**, whenever the device learned about it. Late discovery loses only the time before the device woke.
- Users with `aecenabled` already run the tracker today, so for them activation mostly changes gating, not capture start.
- The pilot measures propagation time (trigger to first accepted point) and reports p50/p95. No in-minute guarantee is promised.
- If Product needs a guaranteed start, the only reliable lever is a visible notification the user acts on; that is a product choice with its own cost.

## Options considered

| Topic | Options | Chosen |
|---|---|---|
| AEC vs advisory | Separate modes · one generic event window · only AEC | One generic event window |
| Schedule owner | Client weekly windows · traveller windows | Traveller windows inside client-enabled types (PRD) |
| Time zone | Device zone · home zone · client zone · derived from location | Device zone, then home, then client |
| Activation | Silent push only · push plus device-side re-evaluation · visible push only | Push plus device-side re-evaluation |

## Consequences

- Schema carries `trigger_type`, `window_minutes` and per-user preference rows (LLD 2.1).
- The base proposal's single client-level schedule table becomes user-level windows plus a client toggle.
- Impact: AEC activation may start later than the trigger on a sleeping device.

## Open items

- Product defines the advisory window and whether AEC and advisory both trigger (owner: Product).
- Product confirms traveller-local vs client-local hours.
- Confirm whether the AEC notification is user-visible on both platforms (owner: Mobile).