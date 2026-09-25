# Backend V2 deployment image for Level5.Api.
#
# Build context is v2/src (not the repo root and not v2/) - Level5.Api only references sibling
# projects under v2/src (Level5.Application -> Level5.Infrastructure -> Level5.Domain, see
# v2/README.md's dependency graph); nothing outside v2/src is needed to restore/build/publish it,
# and nothing in v2/ outside src (tests, tools, docs, scripts) is copied into the image.
#
# Build from the repository root:
#   docker build -f v2/src/Level5.Api/Dockerfile -t level5-v2-api v2/src
#
# This image never applies EF Core migrations - see v2/scripts/build-migration-bundle.ps1 and
# v2/README.md's deployment runbook for the separate migration-bundle artifact and workflow.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore first, from project files only, so editing source doesn't invalidate the restore layer.
COPY Level5.Domain/Level5.Domain.csproj Level5.Domain/
COPY Level5.Application/Level5.Application.csproj Level5.Application/
COPY Level5.Infrastructure/Level5.Infrastructure.csproj Level5.Infrastructure/
COPY Level5.Api/Level5.Api.csproj Level5.Api/
RUN dotnet restore Level5.Api/Level5.Api.csproj

COPY Level5.Domain/ Level5.Domain/
COPY Level5.Application/ Level5.Application/
COPY Level5.Infrastructure/ Level5.Infrastructure/
COPY Level5.Api/ Level5.Api/
# publish (not build then publish) - this Dockerfile isn't used by Visual Studio's container
# debug tooling, which is the only reason the legacy root Dockerfile builds before publishing.
RUN dotnet publish Level5.Api/Level5.Api.csproj -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

# .NET 8+ ASP.NET Core images default ASPNETCORE_HTTP_PORTS=8080 and define a non-root "app" user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=publish /app/publish .
USER app
ENTRYPOINT ["dotnet", "Level5.Api.dll"]
