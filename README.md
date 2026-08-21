# Rekfar — backend

The **Rekfar API** — the single business-logic boundary for
[Rekfar](https://github.com/rekfar/docs), a personal logbook for hiking and climbing in
Norway: a map of where you have been and where you want to go.

The API is an ASP.NET Core **modular monolith**, versioned at `/v1`. The web client is one
consumer; a future native app is another, so business logic lives here and never in a
client ([ADR-0004](https://github.com/rekfar/docs/blob/main/adr/0004-web-first-native-later.md)).

> **Status:** Early. The API host and the **Catalogue** module are in place, serving
> `GET /v1/peaks` — the map-extent query the web client draws its markers from. The rest of
> roadmap [Phase 1](https://github.com/rekfar/docs/blob/main/architecture/06-roadmap.md) —
> account, trip logging, planning, statistics — is still to come.

## Stack

| | |
| --- | --- |
| Runtime | ASP.NET Core Minimal APIs (C#), OpenAPI-generated |
| Data access | EF Core with **NetTopologySuite** |
| Database | Azure SQL Database (free offer), `geography` columns |
| Object storage | Azure Blob Storage — photos and GPX (later phase) |
| Auth | ASP.NET Core Identity, email + password, cookie/JWT |
| Geospatial | NetTopologySuite (geometry), ProjNet (EPSG transforms), a GPX parser |
| Hosting | Azure Container Apps (consumption, scale-to-zero) |
| CI/CD | GitHub Actions |

Chosen in
[ADR-0010](https://github.com/rekfar/docs/blob/main/adr/0010-tech-stack-dotnet-azure-sql.md).

Two constraints from that decision are structural, not preferences:

- **The domain layer uses NetTopologySuite geometry types, never provider-specific spatial
  SQL.** NTS is the same geometry model Npgsql uses for PostGIS, so a later move is a
  provider swap plus a data migration rather than a rewrite.
- **The client is a standalone SPA talking to this API over HTTP only.** No server-rendered
  coupling.

## Getting started

```bash
dotnet run --project src/Rekfar.Api
```

The API needs a database, and this repository does not own the schema — see
[docs/local-development.md](docs/local-development.md) for standing one up, seeding a few
peaks to draw, and the traps worth knowing about first. The endpoint contract is in
[docs/api.md](docs/api.md).

```
src/Rekfar.Api/         Host and composition root: routing, errors, CORS, rate limiting
src/Rekfar.Catalogue/   The Catalogue module — peaks from Kartverket
tests/                  Unit tests, and integration tests against a real database
local/                  Development helpers
infra/                  The Azure footprint as Bicep — see docs/operations.md
```

## This repository does not own the schema

The database schema is defined in the
[database repository](https://github.com/rekfar/database) as a declarative SQL Database
Project, built to a `.dacpac`
([ADR-0013](https://github.com/rekfar/docs/blob/main/adr/0013-schema-owned-by-sql-database-project.md)).

For this repository that means:

- **`dotnet ef migrations` is not used.** Migrations would be a second, competing
  definition of the same schema.
- EF Core maps to those tables explicitly, including the Identity entities
  (`ToTable("User", "auth")` and equivalents) — the default Identity table names are not
  used.
- **Integration tests stand a real database up from the `dacpac` artifact** published by
  the database repository's CI, rather than reconstructing an approximation of it. Drift
  between the two repositories then fails a build instead of surviving unnoticed.

The full contract is in
[database/docs/conventions.md](https://github.com/rekfar/database/blob/main/docs/conventions.md).

## Modules

A modular monolith — one deployable, with clear internal seams
([application architecture §1](https://github.com/rekfar/docs/blob/main/architecture/04-application-architecture.md)):

| Module | Responsibility |
| --- | --- |
| Auth & Account | Registration, login, profile, privacy settings, account deletion |
| Trip & Plan | Trip CRUD, the `planned → completed` transition, private diary notes |
| Wishlist | Peaks and trip ideas the user wants to do |
| Catalogue | Peaks, routes and cabins from Kartverket; search, map-extent and detail queries |
| Statistics | Peaks bagged, total ascent, per year and region |
| Guestbook | Public greetings on a place — kept strictly apart from private diary notes |
| Activity integration | Strava (later Garmin) OAuth, activity ingestion, proposed auto check-ins |
| Import / Export | GPX parsing, full JSON export, account data deletion |
| Reference-data ingestion | Scheduled refresh of the Kartverket datasets |

Later modules (media/photos, connections between users) arrive in Phase 2 and 3.

### API shape

Resource-oriented HTTP/JSON, versioned from day one:

```
/v1/auth   /v1/me       /v1/trips   /v1/trips/{id}/notes   /v1/plans
/v1/peaks  /v1/routes   /v1/cabins  /v1/places/{id}/guestbook
/v1/wishlist  /v1/stats  /v1/activities  /v1/import/gpx  /v1/export
```

Of those, `GET /v1/peaks` is the only one that exists so far — see
[docs/api.md](docs/api.md).

Map-driven endpoints accept a bounding box (`GET /v1/peaks?bbox=…`). All user-data
endpoints require authentication; the catalogue may allow read-only anonymous access.

## Conventions

- **Geometry is stored in WGS84 (EPSG:4326) and nothing else.** Kartverket data arrives in
  ETRS89/UTM 32–35; reprojection happens in application code at ingestion, never in stored
  canonical data.
- **Private by default.** Trips, diary notes and imported activities are the user's; access
  checks are explicit, and private content can never become public content.
- **Norwegian UI, English code and docs.** User-facing strings go through an i18n layer with
  `nb-NO` as the first locale.
- **Attribution.** Kartverket data is CC BY 4.0 — "© Kartverket" is rendered wherever it is
  shown.
- **Tests where correctness matters:** trip status transitions, ascent and stat
  computation, GPX parsing, spatial matching.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request, as jobs
that fail for different reasons on purpose:

- **Build & unit tests** — restore, build (`TreatWarningsAsErrors` makes this the warning
  gate as well), `dotnet format --verify-no-changes` against the `.editorconfig`, and the
  pure tests. No container, no database.
- **Integration tests** — clones the [database repository](https://github.com/rekfar/database)
  at `main`, builds its `dacpac`, and runs the tests against it. They start their own SQL
  Server with Testcontainers and publish that schema into it.
- **Container image** — on pull requests only, builds the image without pushing it, so a
  broken `Dockerfile` is caught in review rather than on `main`. On `main` the deploy job
  builds it anyway, and building it twice would be waste rather than caution.

The schema is built from source rather than downloaded from the database repository's CI:
an artifact of another repository needs a cross-repository token and expires, and building
it needs nothing the job does not already have. It is cloned outside the workspace, because
nested inside it MSBuild would walk up from the `.sqlproj` and apply this repository's
`Directory.Build.props` and central package versions to another repository's build.

Running against that repository's `main` is the point rather than a shortcut: a schema
change that breaks this API fails a build here, which is the drift ADR-0013 accepts two
repositories in exchange for catching. It does mean a red build here can belong to a
commit in the other repository — the job logs the schema commit it tested against, and
`DATABASE_REF` pins one while a coordinated change lands.

## Deployment

A push to `main` whose build **and** integration tests pass deploys itself. The `deploy` job
builds the image, pushes it to Azure Container Registry tagged with the commit SHA, applies
[`infra/main.bicep`](infra/main.bicep) with that tag, and polls `/health` until the new
revision answers.

The whole Azure footprint — registry, managed identity, Container Apps environment and the
app — is in that one template, so the deployed configuration is reviewed in a pull request
like everything else, and a change made in the portal is reconciled away by the next deploy.

Three things about it are decisions rather than defaults:

- **The container app authenticates to the database as a managed identity**, with `SELECT` on
  `[ref]` and nothing else. The logical server is Entra-only, so no password exists anywhere
  in the deployment — there are no Container Apps secrets at all.
- **The runtime image is the `-extra` chiseled variant**, because
  `InvariantGlobalization=false` means the application needs ICU and the bare chiseled image
  has none. It fails at startup without it.
- **`maxReplicas` is 1**, because the rate limiter is in-process: *n* replicas would serve
  *n* × the configured limit.

Creating the resources the first time, the federated credential and its immutable-ID subject
trap, rollback, and what to read when a deploy fails are all in
[docs/operations.md](docs/operations.md).

## Related repositories

| Repository | Contents |
| --- | --- |
| [docs](https://github.com/rekfar/docs) | Architecture, requirements, use cases, ADRs |
| [database](https://github.com/rekfar/database) | The schema — SQL Database Project, deployed as a `.dacpac` |
| [webapp](https://github.com/rekfar/webapp) | React + Vite + TypeScript SPA, MapLibre GL + Kartverket tiles |

Start with the
[Architecture Vision](https://github.com/rekfar/docs/blob/main/architecture/01-architecture-vision.md)
for what Rekfar is and why.
