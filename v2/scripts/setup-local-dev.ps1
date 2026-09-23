<#
.SYNOPSIS
    One-shot local dev setup for Backend V2: starts the shared local Postgres container (the same
    one the legacy backend uses), creates the separate level5_v2 database if it doesn't exist yet,
    generates a fresh JWT signing key, stores both in user-secrets (never in appsettings.json -
    see Program.cs), and applies EF Core migrations. Safe to re-run at any time.

.EXAMPLE
    ./v2/scripts/setup-local-dev.ps1
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$v2Root = Split-Path $PSScriptRoot -Parent

# Must match ../../docker-compose.local-db.yml for the server credentials - level5_v2 is a
# separate database on that same local server, not a separate container. V2's persistence is
# still fully isolated from the legacy "level5" database; this is purely a local-dev convenience
# so contributors don't need to run two Postgres containers. Production may (and likely should)
# point V2 at a genuinely separate instance via ConnectionStrings__DefaultConnection.
$pgUser = "level5"
$pgPassword = "localdevpassword"
$pgDb = "level5_v2"
$pgPort = 5432
$connectionString = "Host=localhost;Port=$pgPort;Database=$pgDb;Username=$pgUser;Password=$pgPassword"

Write-Host "Starting the shared local Postgres container..." -ForegroundColor Cyan
Set-Location $repoRoot
docker compose -f docker-compose.local-db.yml up -d

Write-Host "Waiting for Postgres to accept connections..." -ForegroundColor Cyan
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    docker exec level5-postgres-local pg_isready -U $pgUser *> $null
    if ($LASTEXITCODE -eq 0) {
        $ready = $true
        break
    }
    Start-Sleep -Seconds 1
}
if (-not $ready) {
    throw "Postgres didn't become ready in time - check 'docker logs level5-postgres-local'."
}

Write-Host "Ensuring the level5_v2 database exists..." -ForegroundColor Cyan
docker exec level5-postgres-local psql -U $pgUser -d level5 -tc "SELECT 1 FROM pg_database WHERE datname = '$pgDb'" |
    Select-String -Pattern "1" -Quiet |
    ForEach-Object {
        if (-not $_) {
            docker exec level5-postgres-local psql -U $pgUser -d level5 -c "CREATE DATABASE $pgDb;"
        }
    }

Write-Host "Generating a fresh JWT signing key..." -ForegroundColor Cyan
$jwtKey = & "$repoRoot/scripts/generate-jwt-key.ps1"

Write-Host "Storing secrets via dotnet user-secrets (not written to any file in the repo)..." -ForegroundColor Cyan
Set-Location "$v2Root/src/Level5.Api"
dotnet user-secrets set "ConnectionStrings:DefaultConnection" $connectionString
dotnet user-secrets set "Jwt:Key" $jwtKey

Write-Host "Applying EF Core migrations..." -ForegroundColor Cyan
$env:ConnectionStrings__DefaultConnection = $connectionString
dotnet ef database update --project "$v2Root/src/Level5.Infrastructure" --startup-project "$v2Root/src/Level5.Api"

Write-Host "Done. Run 'dotnet run' from v2/src/Level5.Api to start V2 against your local Postgres." -ForegroundColor Green
