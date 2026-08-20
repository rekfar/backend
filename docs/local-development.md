# Running Rekfar locally

The API needs a database, and this repository does not own the schema — it comes from the
[database repository](https://github.com/rekfar/database) as a `.dacpac`. So local setup is
mostly *that* repository's setup, plus a connection string.

## Prerequisites

- The .NET SDK named in `global.json` (rolls forward on major, so a newer one is fine).
- A container runtime, for SQL Server.
- A checkout of the database repository, ideally as a sibling directory. The integration
  tests look for one.

## 1. A database

The database repository owns this. From your checkout of it:

```bash
cd ../database && local/reset.sh
```

That starts SQL Server in a container, creates the database with the `Norwegian_100_CI_AS`
collation, and publishes the current schema. It needs `local/.env` with an SA password, and
the `sqlpackage` tool.

Three things reliably go wrong here:

- **`sqlpackage` may refuse to launch.** It targets .NET 8, and you probably only have a
  newer SDK. `export DOTNET_ROLL_FORWARD=Major` fixes it without installing an old runtime.
- **`MSSQL_SA_PASSWORD` only applies the first time a data volume is initialised.** If the
  container comes up *unhealthy* with `Login failed for user 'sa'`, an older volume is
  keeping an older password. Either recreate the volume, or change the password in place
  against the stopped container — the second keeps whatever is in the database:

  ```bash
  docker stop rekfar-sql
  docker run --rm -u root --platform linux/amd64 \
      -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="<the password in local/.env>" \
      -v rekfar_rekfar-sql-data:/var/opt/mssql \
      mcr.microsoft.com/mssql/server:2022-latest \
      /opt/mssql/bin/mssql-conf set-sa-password
  docker start rekfar-sql
  ```

- **Microsoft publishes no arm64 SQL Server image.** On Apple Silicon it runs translated.
  With colima, start the VM so it can: `colima start --vm-type vz --vz-rosetta`. It behaves
  identically, including the spatial types the catalogue depends on — it just starts slowly.

## 2. Tell the API about it

The connection string is not in configuration, and the API refuses to start without one:

```bash
dotnet user-secrets set "ConnectionStrings:Rekfar" \
    "Server=localhost,1433;Database=Rekfar;User Id=sa;Password=<password>;TrustServerCertificate=True" \
    --project src/Rekfar.Api
```

In Azure there is no password to store: the logical server is Entra-only and the container
app authenticates with its managed identity.

## 3. Some peaks

A fresh database has none — the catalogue is filled by the Kartverket ingestion job in the
database repository, which downloads a national extract and samples elevations. That is a
lot to ask of a local machine, so this repository ships a handful of peaks instead:

```bash
MSSQL_SA_PASSWORD=<password> local/seed-peaks.sh
```

Seven peaks, every one stamped with a `dev-` external id so it can never collide with a real
SSR `stedsnummer`, and `--clean` removes them again. They are **not** reference data:
coordinates and elevations are approximate, and the real catalogue comes from ingestion.

## 4. Run it

```bash
dotnet run --project src/Rekfar.Api
```

```bash
curl "http://localhost:5199/v1/peaks?bbox=7.5,61.3,8.8,61.8"
```

The generated OpenAPI document is at `/openapi/v1.json` in Development.

## Tests

```bash
dotnet test
```

`Rekfar.Catalogue.Tests` is pure and fast. `Rekfar.Api.IntegrationTests` starts its own SQL
Server with Testcontainers and publishes the dacpac into it, so it needs a container runtime
and a built dacpac — from a sibling checkout of the database repository, or pointed at with
`REKFAR_DACPAC`. Expect the first run to spend around half a minute starting SQL Server.

## A trap worth knowing: hidden files

**On macOS, everything under a `.claude/worktrees/` checkout carries the `UF_HIDDEN` flag.**
`PhysicalFileProvider` defaults to `ExclusionFilters.Sensitive`, which skips hidden files,
and `appsettings.json` is registered as optional — so the application starts having loaded
**no configuration at all**, with no error and no warning.

What it looks like: CORS refuses to start despite `appsettings.Development.json` naming an
origin; every configured default silently reverts. Confirm it with:

```bash
ls -lO src/Rekfar.Api/appsettings.json    # 'hidden' in the flags column
```

`chflags -R nohidden .` clears it, but the tooling re-applies it. Running from a normal
checkout is unaffected, and Linux has no equivalent flag, so containers and CI never see it.
