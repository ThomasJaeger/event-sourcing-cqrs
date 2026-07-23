# Chapter 18 migration-patterns demo image.
#
# Build from the repository root, not from this directory:
#   docker build -f src/Migration/Dockerfile .
# Migration.Demo references src/Domain, src/Application, and
# src/Infrastructure/EventStore.Postgres, so the build stage needs the whole src tree.
# src/Migration/docker-compose.yml sets the context accordingly.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/Migration/Migration.Demo/Migration.Demo.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Migration.Demo.dll"]
