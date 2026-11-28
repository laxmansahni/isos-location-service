# ISOS Location Service

Backend for Live Location Sharing. The Assistance App sends consent and location points to this service, and managers see them in Quantum through the same API. It enforces consent and client sharing policy on the server, stores history for the retention period, and publishes one event per accepted point for Location Confidence.

Status: **design in review, implementation starting.** The build-vs-buy, storage and stack decisions (ADR-001, 010, 011) are Accepted; the rest are Proposed until their open questions are answered.

## Documentation

Read in this order:

| Document | Contents |
|---|---|
| [Requirements baseline](docs/live-location-sharing-requirements.md) | Problem statement, source stories, requirement IDs, conflicts between sources |
| [HLD](docs/live-location-sharing-backend-architecture.md) | Context, components, flows, integration boundaries, risks, open questions |
| [LLD](docs/live-location-sharing-lld.md) | Data model, API contracts, algorithms, event schemas |
| [ADRs](docs/adr/README.md) | One decision per record (ADR-001 to ADR-011) |
| [Review responses](docs/live-location-sharing-review-responses.md) | Answers to the Architecture team's questions |

Diagram sources and PNGs are in [docs/diagrams](docs/diagrams). Re-render a PNG from its `.mmd` file whenever the Mermaid block in the LLD or HLD changes.

Tracking: epic SX-76, story SUB-5675.

## Repository layout

```
ISOS.LocationService.sln
src/ISOS.LocationService/   ASP.NET Core Web API (net10.0, controllers, OpenAPI)
src/ISOS.LocationService.Purge/   Purge job: a console app that runs once and exits
tests/ISOS.LocationService.Tests/   xUnit unit, endpoint and PostgreSQL tests
docs/                       Architecture documents
```

The target design is one deployable with three modules (ingestion, policy, query) and a separate purge job (`ISOS.LocationService.Purge`).

## Getting started

Prerequisite: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
dotnet restore ISOS.LocationService.sln
dotnet build ISOS.LocationService.sln --no-restore
dotnet run --project src/ISOS.LocationService
dotnet test ISOS.LocationService.sln
```

In Development the app listens on `http://localhost:5027` and serves the OpenAPI document at `/openapi/v1.json`. There is no real authentication yet.

## API

| Method | Path | Status |
|---|---|---|
| POST | `/v1/locationtracking/points` | Implemented (validation, idempotency, PostgreSQL storage) |
| GET | `/v1/locationtracking/policy` | Implemented (effective policy, ETag/`304`) |
| PUT | `/v1/locationtracking/consent` | Implemented (grant, revoke with visibility fence) |
| PUT | `/v1/locationtracking/devices/{deviceId}/state` | Implemented (OS permission, time zone, lease) |
| DELETE | `/v1/locationtracking/devices/{deviceId}/session` | Implemented (logout) |
| PUT | `/v1/locationtracking/users/me/preference` | Implemented (type, schedule, home zone) |
| GET | `/v1/locationtracking/latest` | Implemented (shared, fresh points; ETag/`304`, `since`); no population scope or names yet |
| GET | `/v1/locationtracking/users/{id}/history` | Implemented (ranges, cursor paging, access audit); no population scope yet |
| GET | `/v1/locationtracking/users/{id}/replay` | Implemented for location shares only; check-in and AEC sources not wired |

Try it locally. The `partyId` header is a Development-only placeholder for the authenticated person until token validation exists (HLD Q8). The client will be resolved from the party on the server; until then the Development-only `clientId` header names it, and it is needed for everything except not-shared points. In the same way the Development-only `permissions` header carries the function codes (`MGRLOCHIS`, `USRLOCHIS`) that the token will carry later.

Live sharing needs a consent and a chosen type before points are accepted, so a first run is three calls (a client without a row in `client_location_policy` uses `Policy:DefaultClient`, which `appsettings.Development.json` turns on):

```sh
H='-H partyId:IND_335dc59b-2b6a-4fa9-976f-d0c2b3d0647f -H clientId:client-1 -H Content-Type:application/json'
curl -X PUT http://localhost:5027/v1/locationtracking/consent $H -d '{"status":"GRANTED","disclosureVersion":"1"}'
curl -X PUT http://localhost:5027/v1/locationtracking/users/me/preference $H -d '{"primaryType":"ALWAYS"}'
curl http://localhost:5027/v1/locationtracking/policy $H
```

Then send a point:

```sh
curl -X POST http://localhost:5027/v1/locationtracking/points \
  -H "Content-Type: application/json" \
  -H "partyId: IND_335dc59b-2b6a-4fa9-976f-d0c2b3d0647f" \
  -H "clientId: client-1" \
  -d '{"points":[{"pointId":"7f1c0000-0000-0000-0000-000000000001","capturedAt":"2026-11-19T09:00:00Z","latitude":51.5074,"longitude":-0.1276,"accuracy":12.5,"shared":true}],"device":{"id":"device-1","platform":"ios"}}'
```

Use a recent `capturedAt`: points older than the retention window (14 days) are rejected, and so is a live point captured before you granted consent (`CAPTURED_BEFORE_CONSENT`), so replace the date above with a time just after the consent call. The full contract is in the [LLD](docs/live-location-sharing-lld.md#4-api).

Read the data back (a manager looking at the person who sent the point above):

```sh
curl "http://localhost:5027/v1/locationtracking/users/IND_335dc59b-2b6a-4fa9-976f-d0c2b3d0647f/history?range=24h" \
  -H "partyId: IND_manager" -H "permissions: MGRLOCHIS"
curl http://localhost:5027/v1/locationtracking/latest -H "partyId: IND_manager" -H "permissions: MGRLOCHIS"
```

Reading your own data needs `USRLOCHIS`; reading someone else's needs `MGRLOCHIS` and writes an access-audit row (table `access_audit`, no coordinates). Only shared points are ever returned.

**Required per point:** `pointId`, `capturedAt`, `latitude`, `longitude`, `accuracy` and `shared`. `shared` is `true` when live location sharing is on for the point and `false` when it was captured only for AEC or Location Based Alerts. It has no default: a point without it is rejected with `MISSING_FIELD`. A `shared=true` point is added to history and becomes the latest position. A `shared=false` point is never added to history; it only replaces the latest position (table `latest_location`) when its `capturedAt` is newer.

**Statuses per point:** `Stored`, `Duplicate`, `Superseded` (a not-shared point that is not newer than the latest position, so it was dropped) and `Rejected`. A device may delete a point in every status.

### OpenAPI document

The machine-readable contract is committed at [docs/api/openapi-v1.json](docs/api/openapi-v1.json). It can be imported into Postman or used to generate client types. Regenerate it after any contract change: start the service in Development (`Database__UseInMemory=true` avoids needing a database), save `http://localhost:5027/openapi/v1.json` over that file, and remove the `servers` entry.

## Database

The location database (`locationDB`) is **Aurora PostgreSQL** in International SOS's AWS account ([ADR-010](docs/adr/ADR-010-storage-engine.md)). For development, install [PostgreSQL](https://www.postgresql.org/) **18.x** locally (for example `docker run --name locationdb -e POSTGRES_PASSWORD=<choose-a-password> -e POSTGRES_DB=locationdb -p 5432:5432 -v locationdb-data:/var/lib/postgresql -d postgres:18`) and create an empty `locationdb` database. Aurora PostgreSQL supports 18, and the `locationDB` cluster should run the same major version (Platform to confirm). Schema changes are versioned migrations in this repository, run the same way locally and in AWS.

- Use only features Aurora PostgreSQL supports. No TimescaleDB.
- Migrations are plain SQL scripts in `src/ISOS.LocationService/Database/Migrations`, applied by DbUp. In Development they run automatically at startup (`Database:RunMigrationsOnStartup`); elsewhere they are a separate deployment step. The service also creates the daily history partitions (15 days back to 7 days ahead) at startup and every 6 hours.

Point the service at your local database with a git-ignored settings file next to the project. No command is needed, and it works the same in Rider and Visual Studio:

1. Copy `src/ISOS.LocationService/appsettings.Local.example.json` to `src/ISOS.LocationService/appsettings.Local.json` in the same folder.
2. Open the copy and replace `<your-password>` with the password of your local PostgreSQL `postgres` user (adjust `Host`, `Port` or `Username` if yours differ).
3. Start the service from your IDE.

`appsettings.Local.json` is listed in `.gitignore`, so Git will not stage it, and it is excluded from published output. Only the `.example.json` template is committed, and it must never contain a real password. The file is read only in the Development environment. A shared or production password must never be put in it: those environments get the connection string from the deployment (the `ConnectionStrings__LocationDb` environment variable, filled from the AWS secret store).

Alternatives that also work: `dotnet user-secrets set "ConnectionStrings:LocationDb" "<connection string>" --project src/ISOS.LocationService`, or the same environment variable in your run configuration.

If no connection string is set, Development falls back to an in-memory store (data is lost on restart) and logs a warning; `"Database": { "UseInMemory": true }` in the local file forces this even when a connection string exists. Any other environment refuses to start without a connection string.

The automated tests run in a separate `Testing` environment that ignores your local file and user secrets. They start their own disposable PostgreSQL 18 containers through Docker, so Docker must be running for `dotnet test`.

### Purge job

`ISOS.LocationService.Purge` enforces retention (LLD 5.3). One run deletes data captured before a revoked consent's or a disabled client's fence, deletes rows past `retention_expires_at`, drops old empty history partitions, and records the outcome in table `purge_run`. It runs once and exits with `0` (done, or another run was already going) or `1` (failed), so a daily schedule just starts it. It needs the migrations applied first (start the API once in Development, or run them as your deployment step).

```sh
dotnet run --project src/ISOS.LocationService.Purge
```

In Development it reads the same `appsettings.Local.json` as the API, so no second password file is needed; elsewhere it uses the `ConnectionStrings__LocationDb` environment variable. Settings (section `Purge`): `BatchSize` (10,000 rows per delete) and `PartitionLockTimeout` (5 s). It uses `Ingestion:RetentionDays` when that is set, so keep one value for both. Check what it did:

```sh
docker exec -it locationdb psql -U postgres -d locationdb -c "SELECT started_at, status, expired_history_rows, fenced_history_rows, expired_latest_rows, fenced_latest_rows, cardinality(dropped_partitions) AS dropped FROM purge_run ORDER BY started_at DESC LIMIT 5;"
```

It deletes real rows, so on a shared database run it deliberately.

- Local data is synthetic only. Never copy production location data to a laptop.
- Do not commit credentials or connection strings.

## Contributing

- Work on a branch named `SUB-<number>-<few-words>`.
- Never log coordinates. Log point IDs instead.
- Keep contract and behaviour changes in step with the LLD and the relevant ADR.

## Contacts

Repository owner: Laxman Sahni.
