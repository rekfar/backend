#!/usr/bin/env bash
#
# Seed a handful of development peaks into the local Rekfar database, so the map has
# something to draw without running the full Kartverket import.
#
# The database itself belongs to the database repository. Stand it up there first:
#
#     cd ../database && local/reset.sh
#
# This script only talks to the container that leaves running.
#
# Requires: docker, and the SA password the database repository's local/.env sets.
#
# Usage:
#     MSSQL_SA_PASSWORD=... local/seed-peaks.sh            # insert or update the seed rows
#     MSSQL_SA_PASSWORD=... local/seed-peaks.sh --clean    # remove them again
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

CONTAINER="rekfar-sql"
DB_NAME="Rekfar"
CLEAN=0

for arg in "$@"; do
    case "$arg" in
        --clean) CLEAN=1 ;;
        *) echo "Unknown argument: $arg" >&2; exit 2 ;;
    esac
done

if [[ -z "${MSSQL_SA_PASSWORD:-}" ]]; then
    echo "MSSQL_SA_PASSWORD is not set. It is the password in the database repository's" >&2
    echo "local/.env — the same one local/reset.sh starts the container with." >&2
    exit 1
fi

if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
    echo "The '$CONTAINER' container is not running. Start it from the database" >&2
    echo "repository first:  cd ../database && local/reset.sh" >&2
    exit 1
fi

# -b so a failed batch exits non-zero rather than reporting success, -C to accept the
# container's self-signed certificate.
sqlcmd() {
    docker exec -i "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
        -C -b -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d "$DB_NAME" "$@"
}

if [[ $CLEAN -eq 1 ]]; then
    echo "==> Removing development peaks"
    sqlcmd -Q "DELETE FROM [ref].Peak WHERE ExternalId LIKE 'dev-%';"
    echo "==> Done."
    exit 0
fi

echo "==> Seeding development peaks into $DB_NAME"
sqlcmd < local/seed-peaks.sql

cat <<'NOTE'

==> Done. Point the API at this database and query an extent over Jotunheimen:

    dotnet user-secrets set "ConnectionStrings:Rekfar" \
        "Server=localhost,1433;Database=Rekfar;User Id=sa;Password=$MSSQL_SA_PASSWORD;TrustServerCertificate=True" \
        --project src/Rekfar.Api

    curl "http://localhost:5199/v1/peaks?bbox=7.5,61.3,8.8,61.8"

NOTE
