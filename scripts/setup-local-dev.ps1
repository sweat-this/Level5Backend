<#
.SYNOPSIS
    One-shot local dev setup: starts the local Postgres container, generates a fresh JWT signing
    key, stores both in user-secrets (never in appsettings.json - see Program.cs), and applies EF
    Core migrations. Safe to re-run at any time - re-running just rotates the JWT key and leaves
    Postgres/its data alone.

.EXAMPLE
    ./scripts/setup-local-dev.ps1
#>

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

# These must match docker-compose.local-db.yml - if you change one, change the other.
$pgUser = "level5"
$pgPassword = "localdevpassword"
$pgDb = "level5"
$pgPort = 5432
$connectionString = "Host=127.0.0.1;Port=$pgPort;Database=$pgDb;Username=$pgUser;Password=$pgPassword"

Write-Host "Starting local Postgres container..." -ForegroundColor Cyan
docker compose -f docker-compose.local-db.yml up -d

Write-Host "Waiting for Postgres to accept connections..." -ForegroundColor Cyan
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    docker exec level5-postgres-local pg_isready -U $pgUser -d $pgDb *> $null
    if ($LASTEXITCODE -eq 0) {
        $ready = $true
        break
    }
    Start-Sleep -Seconds 1
}
if (-not $ready) {
    throw "Postgres didn't become ready in time - check 'docker logs level5-postgres-local'."
}

Write-Host "Generating a fresh JWT signing key..." -ForegroundColor Cyan
$jwtKey = & "$PSScriptRoot/generate-jwt-key.ps1"

Write-Host "Storing secrets via dotnet user-secrets (not written to any file in the repo)..." -ForegroundColor Cyan
dotnet user-secrets set "ConnectionStrings:DefaultConnection" $connectionString
dotnet user-secrets set "Jwt:Key" $jwtKey

Write-Host "Applying EF Core migrations..." -ForegroundColor Cyan
dotnet ef database update

Write-Host "Done. Run 'dotnet run' to start the API against your local Postgres." -ForegroundColor Green
