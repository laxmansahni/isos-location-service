# ADR-002: Retention equals the longest queryable range; 14 days by default

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-06 |
| **Requirements** | FR-10, FR-11, FR-15; conflicts C1, C2 |

## Context

Sources disagree:

| Source | Retention | Query / replay window |
|---|---|---|
| SUB-5675 AC 6 (SUB-5363) | 30 days default, or per client policy | – |
| SUB-5675 AC 4 and 5 (SUB-5371, SUB-5364) | – | 1 h (default), 24 h, 7 d for both history and replay |
| PRD (via HLD v0.2) | 14 days default | 1 h, 24 h, 7 d, 14 d for replay |
| Base proposal (before this change) | 30 days | 1 h, 24 h, 7 d |

With 30 days retained and 7 days queryable, **23 days of data have no read path**. This is inconsistent inside SUB-5675 itself, before the PRD is considered. Nothing in the requirements needs them: the access audit is stored separately and holds no coordinates, and a data-subject access request is served from whatever is stored. Keeping data nobody can use is a data-minimisation and storage-limitation problem and adds cost.

## Decision drivers

- Store personal location no longer than the product needs (privacy).
- The product must be able to show everything it keeps.
- Per-client override remains possible.
- Purge must be cheap and provable.

## Options

| Option | Retention | Queryable | Result |
|---|---|---|---|
| 1. Keep SUB-5363 | 30 d | 7 d | Unused data for 23 days. Rejected |
| 2. Keep 30 d and extend ranges to 30 d | 30 d | 30 d | Matches ticket; exceeds PRD; larger store and wider exposure |
| 3. **Align to PRD** | 14 d | 14 d | Retention equals the longest range |

## Decision

**Option 3, with an invariant.**

1. Default `retention_days = 14` (PRD). SUB-5363 and SUB-5371 are amended by Product to 14 d and add the 14 d range.
2. **Invariant:** `retention_days >= longest enabled query range`. The admin API rejects settings that break it.
3. Allowed query ranges are `1h`, `24h`, `7d`, `14d`, each capped at the client's `retention_days`.
4. A client may shorten retention. Replay shortens with it, and expired data is purged at the next purge run.
5. A longer retention needs Product and Legal approval. If approved, the longest range grows to match; we do not retain data that cannot be queried.
6. Check-ins and AECs keep their own retention in their own stores. Replay returns what those stores still hold.

## Consequences

- Storage at the planning assumption (HLD 9): about 29 M points per day × 14 d ≈ 406 M rows ≈ 60 GB, half of the 30 d figure.
- Daily partitions drop after 14 days plus the purge grace; the purge job honours per-client shorter retention by `retention_expires_at`.
- The ticket text must change, otherwise QA will test 30 d.

## Open items

- Product confirms 14 d and the 14 d range (owner: Product).
- Legal and **Privacy** set the maximum a client may select. The Location Confidence PRD requires its location history retention to match existing mechanisms and to be approved by the Privacy team, so the same approval is needed here (requirements L5).
- Location Confidence keeps its own signal ledger. Live points in it must not outlive this retention and must honour the fence and erasure (requirements C13).
- Whether history (table) also offers 14 d, or only replay (the PRD names replay).