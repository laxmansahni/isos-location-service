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
tests/ISOS.LocationService.Tests/   xUnit unit and endpoint tests
docs/                       Architecture documents
```

The target design is one deployable with three modules (ingestion, policy, query) and a separate purge job. A worker project for the purge job will be added when it is built.

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
| GET | `/v1/locationtracking/latest` | Implemented (shared, fresh points; ETag/`304`, `since`); no population scope or names yet |
| GET | `/v1/locationtracking/users/{id}/history` | Implemented (ranges, cursor paging, access audit); no population scope yet |
| GET | `/v1/locationtracking/users/{id}/replay` | Implemented for location shares only; check-in and AEC sources not wired |

Try it locally. The `partyId` header is a Development-only placeholder for the authenticated person until token validation exists (HLD Q8); the client is not sent and will be resolved from the party on the server. In the same way the Development-only `permissions` header carries the function codes (`MGRLOCHIS`, `USRLOCHIS`) that the token will carry later:

```sh
curl -X POST http://localhost:5027/v1/locationtracking/points \
  -H "Content-Type: application/json" \
  -H "partyId: IND_335dc59b-2b6a-4fa9-976f-d0c2b3d0647f" \
  -d '{"points":[{"pointId":"7f1c0000-0000-0000-0000-000000000001","capturedAt":"2026-11-19T09:00:00Z","latitude":51.5074,"longitude":-0.1276,"accuracy":12.5,"shared":true}],"device":{"id":"device-1","platform":"ios"}}'
```

Use a recent `capturedAt`: points older than the retention window (14 days) are rejected. The full contract is in the [LLD](docs/live-location-sharing-lld.md#4-api).

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

- Local data is synthetic only. Never copy production location data to a laptop.
- Do not commit credentials or connection strings.

## Contributing

- Work on a branch named `SUB-<number>-<few-words>`.
- Never log coordinates. Log point IDs instead.
- Keep contract and behaviour changes in step with the LLD and the relevant ADR.

## Contacts

Repository owner: Laxman Sahni.
