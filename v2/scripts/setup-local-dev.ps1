<#
.SYNOPSIS
    One-shot local dev setup for Backend V2: starts the shared local Postgres container (the same
    one the legacy backend uses), creates the separate level5_v2 database if it doesn't exist yet,
    applies EF Core migrations, generates a fresh JWT signing key, and stores the connection string
    and key in user-secrets (never in appsettings.json - see Program.cs). Safe to re-run at any time.

    Day-to-day database commands (start/stop/migrate/reset) live in db.ps1.

.EXAMPLE
    ./v2/scripts/setup-local-dev.ps1
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$v2Root = Split-Path $PSScriptRoot -Parent

# level5_v2 is a separate database on the shared local server, not a separate container. V2's
# persistence is still fully isolated from the legacy "level5" database; this is purely a local-dev
# convenience so contributors don't need to run two Postgres containers. Production may (and likely
# should) point V2 at a genuinely separate instance via ConnectionStrings__DefaultConnection.
& "$PSScriptRoot/db.ps1" migrate
$connectionString = & "$PSScriptRoot/db.ps1" connection-string

Write-Host "Generating a fresh JWT signing key..." -ForegroundColor Cyan
$jwtKey = & "$repoRoot/scripts/generate-jwt-key.ps1"

Write-Host "Storing secrets via dotnet user-secrets (not written to any file in the repo)..." -ForegroundColor Cyan
dotnet user-secrets set "ConnectionStrings:DefaultConnection" $connectionString --project "$v2Root/src/Level5.Api"
dotnet user-secrets set "Jwt:Key" $jwtKey --project "$v2Root/src/Level5.Api"

Write-Host "Done. Run 'dotnet run' from v2/src/Level5.Api to start V2 against your local Postgres." -ForegroundColor Green
