# The API as a container image, for Azure Container Apps (ADR-0010).
#
# Two stages: the SDK restores, builds and publishes; only the published output is copied
# into a runtime image that carries no compiler, no source and no package cache.

# Declared before the first FROM so both stages read the same version. The SDK itself is
# pinned by global.json, which rolls forward on major — this tag only has to be no older.
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore before the sources are copied, so editing a .cs file reuses this layer rather than
# re-downloading every package. These are exactly the files a restore reads: global.json
# pins the SDK, Directory.Packages.props holds every version (central package management),
# and Directory.Build.props is where TargetFramework and the strictness flags live.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Rekfar.Api/Rekfar.Api.csproj src/Rekfar.Api/
COPY src/Rekfar.Catalogue/Rekfar.Catalogue.csproj src/Rekfar.Catalogue/

# The host project, not the solution: the tests are not in the image, and restoring them
# would pull DacFx and Testcontainers for nothing. Project references restore transitively,
# so this covers Rekfar.Catalogue.
RUN dotnet restore src/Rekfar.Api/Rekfar.Api.csproj

COPY src/ src/

# TreatWarningsAsErrors is on for every project, so this is as strict as the CI build.
RUN dotnet publish src/Rekfar.Api/Rekfar.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app

# -extra, not the bare chiseled image, and this is load-bearing. Directory.Build.props sets
# InvariantGlobalization=false deliberately — Norwegian sorting, nb-NO formatting and the
# accent-insensitive name matching the catalogue depends on all need ICU. The bare chiseled
# image ships without ICU, and the application then throws at startup. The -extra variant is
# the same minimal, shell-less, non-root image plus ICU and tzdata.
#
# Never set DOTNET_SYSTEM_GLOBALIZATION_INVARIANT here or in the container app's environment.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS runtime
WORKDIR /app

COPY --from=build /app ./

# 8080 is the ASP.NET Core default in a container and what the ingress in infra/main.bicep
# targets. Declared here so the two cannot drift apart unnoticed.
EXPOSE 8080

# No USER line: the chiseled images already run as a non-root user (UID 1654).
ENTRYPOINT ["dotnet", "Rekfar.Api.dll"]
