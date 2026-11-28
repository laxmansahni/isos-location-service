# ADR-010: Storage engine – Aurora PostgreSQL (local PostgreSQL for development)

| | |
|---|---|
| **Status** | Accepted (engine confirmed by the project owner, 2026-11-19; version and extensions still to confirm with Platform) |
| **Date** | 2026-11-06, updated 2026-11-19 |
| **Requirements** | FR-11, FR-15; NFR-01, NFR-04, NFR-05, NFR-06 |

## Context

Architecture's HLD v0.2 proposed PostgreSQL. An earlier version of this ADR said PostgreSQL was not a platform standard; that was wrong, based only on the mobile middleware's `Web.config`, which shows SQL Server for that service.

**Confirmed (2026-11-19):** International SOS uses **Aurora PostgreSQL** for the location database (`locationDB`). Development runs against a local PostgreSQL from [postgresql.org](https://www.postgresql.org/); once it works locally, the service is deployed to International SOS's AWS account against Aurora PostgreSQL. The Location Confidence PRD's risk "TimescaleDB is not supported on the Aurora…" (UNICORN-75) agrees: Aurora PostgreSQL is in use and TimescaleDB is not available, so we rely on native partitioning.

**Capacity matters for this choice.** The Location Confidence PRD requires ingestion for at least 500,000 people with 4× spikes. At 500,000 sharers and one point per 5 minutes, writes average about 1,670/s and peak near 8,300/s, about 144 M rows per day and about 2.0 B rows (roughly 300 GB) over 14 days (HLD 9). That is five times the earlier planning figure and needs an early load test on the chosen engine.

## Decision drivers

- A transactional outbox: persist the point and the event together.
- Purge that is cheap and can be evidenced (GDPR workflow).
- Per-client retention and population-scoped reads.
- Existing skills, tooling, backups and operations.
- Headroom: the planning assumption is about 330 writes/s average and 1,700/s peak, about 406 M rows at 14 days (HLD 9).

## Options

| Option | Fit |
|---|---|
| A. **Aurora PostgreSQL** for policy, consent, history, device sessions, outbox, audit, and the latest-position table | **Chosen.** Outbox and policy in one transaction; native declarative partitioning, so expired days are detached and dropped; `INSERT … ON CONFLICT DO NOTHING` for idempotency; joins for scope and audit; already the platform's location database and Location Confidence's engine. Needs partition creation and retirement jobs; vertical scaling limit |
| B. SQL Server with the same shape | Not used. Considered only because the middleware runs it; the location database is Aurora PostgreSQL |
| C. DynamoDB for everything | Elastic and no partition maintenance; but TTL deletion is eventual and hard to evidence, per-client retention and population queries need custom indexes, and the outbox relies on streams |
| D. **Hybrid**: relational store for policy, consent, audit, outbox and history; DynamoDB only for the latest-position projection | Fast key reads for the map; one more store to operate, back up, erase and keep in the region |

## Decision

**Aurora PostgreSQL** for policy, consent, history, device sessions, outbox, audit and the latest-position table, in the `locationDB` database. SQL Server and DynamoDB are not used for this service. At about 100,000 rows the latest-position table needs no separate store. If measured write rate or read latency later exceeds what Aurora can serve, revisit Option D (DynamoDB for the latest-position projection only) and keep the write-path interface so the move does not change the API.

Detail (partitioning, idempotency index) is in [LLD 2.2 and 2.5](../live-location-sharing-lld.md).

### Local development and parity with Aurora

- Local development uses **PostgreSQL 18.x** from postgresql.org (decided 2026-11-19). Aurora PostgreSQL offers 18 (AWS release notes list 18.3, 18.4 and 18.6; the latest listed is 18.6.1 of 15 October 2026), so 18 is available for a new cluster. The `locationDB` cluster itself must run the same major version; Platform confirms it, and if the existing cluster is on an older major version, local development should match that version. Aurora is PostgreSQL-compatible but not identical, so features are limited to those Aurora supports.
- Use plain PostgreSQL features only: declarative range partitioning, `INSERT … ON CONFLICT DO NOTHING`, and `pg_partman`-style maintenance done by our own job unless Platform confirms the extension is available on Aurora. No TimescaleDB.
- Schema is created by versioned migrations kept in this repository and run the same way locally and in AWS. Local data is synthetic only; never copy production location data to a laptop.
- Connection strings and passwords come from user secrets or environment variables locally and from the AWS secret store in deployment. Nothing is committed.

## Consequences

- A partitioned unique index must include the partition column, so the idempotency key is `(user_id, captured_at, point_id)`.
- The LLD's engine mapping for SQL Server is no longer needed and is removed.
- Partition creation and retirement are our job (scheduled), as is purge evidence; backups fall under the erasure question (Platform, HLD Q13).
- Data access uses Npgsql with plain SQL, and migrations are plain SQL scripts embedded in the service and run by DbUp (ADR-011). The first migration (`0001_create_location_history.sql`) creates the partitioned history table and a function that creates daily partitions.
- The service creates partitions for 15 days back to 7 days ahead at startup and every 6 hours. Purging expired data is the separate purge job (`ISOS.LocationService.Purge`, LLD 5.3).

## Open items

- The major version of the `locationDB` cluster (it should be 18 to match local development; Aurora supports 18), instance class and Multi-AZ/replica setup (Platform).
- Whether `pg_partman` or other extensions are available on Aurora.
- Capacity test at peak, using both the 100,000 and 500,000 sharer scenarios (HLD 9), on Aurora, not only locally.
- Backup retention and erasure of snapshots (HLD Q13).