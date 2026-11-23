# Architecture Decision Records – Live Location Sharing

Each ADR records one decision, the options considered and the consequences. ADR-001, ADR-010 and ADR-011 are **Accepted** (decided by the project owner and Architecture). The rest are **Proposed** until the open questions they depend on, mostly with Product, Legal and the Location Confidence team, are answered. Detail such as payloads and columns lives in the [LLD](../live-location-sharing-lld.md); the overview is in the [HLD](../live-location-sharing-backend-architecture.md); the problem and requirements are in the [requirements baseline](../live-location-sharing-requirements.md).

| ADR | Decision | Status | Resolves |
|---|---|---|---|
| [001](./ADR-001-location-service-build-vs-buy.md) | Build an in-house Location Service rather than use Vismo, Amazon Location or another vendor | Accepted | Build vs buy, vendor comparison |
| [002](./ADR-002-retention-and-query-window.md) | Retention equals the longest queryable range; 14 days by default | Proposed | C1, C2 (30 d vs 14 d vs 7 d) |
| [003](./ADR-003-capture-strategy.md) | One low-power capture profile with a minimum spacing; no high-frequency mode | Proposed | C3 (150 m vs high-frequency) |
| [004](./ADR-004-sharing-modes-and-local-time.md) | Sharing modes, event-window activation (AEC or advisory), local-time schedule, silent-push reliability | Proposed | C4, C5, schedule hours, iOS push |
| [005](./ADR-005-revoke-disable-and-client-change.md) | Revoke, client disable, OS permission loss and client change | Proposed | C7, stored data, cache invalidation |
| [006](./ADR-006-event-integration-and-lba.md) | Integration with Location Confidence, Impact and location-based alerting | Proposed | Alerting, Location Confidence |
| [007](./ADR-007-data-residency.md) | Store in the client's data region | Proposed | User region vs client region |
| [008](./ADR-008-multiple-devices.md) | Newest allowed point across a user's sharing devices | Proposed | Device priority |
| [009](./ADR-009-manager-map-transport.md) | Delta polling first; own gateway later; no Hasura Cloud | Proposed | Transport |
| [010](./ADR-010-storage-engine.md) | Aurora PostgreSQL for history and policy; local PostgreSQL for development | Accepted | Storage engine |
| [011](./ADR-011-implementation-stack.md) | C# on .NET 10 with ASP.NET Core controllers | Accepted | Implementation stack |

Template: Status · Context · Decision drivers · Options · Decision · Consequences · Open items.
