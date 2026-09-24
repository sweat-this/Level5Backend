<#
.SYNOPSIS
    Local Backend V2 database workflow against the shared dev Postgres container defined in
    ../../docker-compose.local-db.yml.

.DESCRIPTION
    start             Start the local Postgres container and wait until its health check passes.
    stop              Stop the container. Data is kept (the volume is not touched). This also stops
                      the legacy backend's "level5" database, which lives on the same server.
    migrate           Start (if needed), create the level5_v2 database if missing, and apply the
                      current V2 EF Core migrations to it.
    reset             DESTRUCTIVE. Drop and recreate ONLY the local level5_v2 database, then apply
                      migrations. The legacy "level5" database and the volume are left alone.
    connection-string Print the local level5_v2 connection string (used by setup-local-dev.ps1).

    Every command operates on the local compose container only. The target is built from the
    LEVEL5_PG_* settings (environment or the git-ignored ../../.env - see ../../.env.example) and
    is always 127.0.0.1; ConnectionStrings__DefaultConnection is never read, so this script
    cannot be pointed at a shared, staging, or production database. Schema lives in EF Core
    migrations, never in this script.

.EXAMPLE
    ./v2/scripts/db.ps1 migrate

.EXAMPLE
    ./v2/scripts/db.ps1 reset -Force
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("start", "stop", "migrate", "reset", "connection-string")]
    [string]$Command,

    # Skips the interactive confirmation for "reset".
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$v2Root = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $v2Root -Parent
$composeFile = Join-Path $repoRoot "docker-compose.local-db.yml"
$database = "level5_v2"

# Same values docker compose resolves: environment first, then .env, then the compose defaults.
function Get-Setting([string]$name, [string]$default) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value) { return $value }
    $envFile = Join-Path $repoRoot ".env"
    if (Test-Path $envFile) {
        $line = Get-Content $envFile | Where-Object { $_ -match "^\s*$name\s*=" } | Select-Object -Last 1
        if ($line) { return ($line -split "=", 2)[1].Trim().Trim('"', "'") }
    }
    return $default
}

$pgUser = Get-Setting "LEVEL5_PG_USER" "level5"
$pgPassword = Get-Setting "LEVEL5_PG_PASSWORD" "localdevpassword"
$pgPort = Get-Setting "LEVEL5_PG_PORT" "5432"
$connectionString = "Host=127.0.0.1;Port=$pgPort;Database=$database;Username=$pgUser;Password=$pgPassword"

function Invoke-Compose {
    docker compose -f $composeFile @args
    if ($LASTEXITCODE -ne 0) { throw "docker compose $args failed (exit code $LASTEXITCODE)." }
}

function Invoke-Psql([string]$sql) {
    # Always against the maintenance database "level5", never the one being created/dropped.
    $output = docker compose -f $composeFile exec -T postgres psql -U $pgUser -d level5 -v ON_ERROR_STOP=1 -tAc $sql
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $sql" }
    return $output
}

function Start-Database {
    Write-Host "Starting local Postgres and waiting for it to report healthy..." -ForegroundColor Cyan
    Invoke-Compose up -d --wait
}

function Initialize-Database {
    if ((Invoke-Psql "SELECT 1 FROM pg_database WHERE datname = '$database'") -ne "1") {
        Write-Host "Creating database $database..." -ForegroundColor Cyan
        Invoke-Psql "CREATE DATABASE $database" | Out-Null
    }
}

function Update-Database {
    Write-Host "Applying V2 EF Core migrations to $database on 127.0.0.1:$pgPort..." -ForegroundColor Cyan
    $previous = $env:ConnectionStrings__DefaultConnection
    try {
        # Read only by Level5V2DbContextFactory (design-time); scoped to this call.
        $env:ConnectionStrings__DefaultConnection = $connectionString
        dotnet ef database update --project "$v2Root/src/Level5.Infrastructure" --startup-project "$v2Root/src/Level5.Api"
        if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update failed (exit code $LASTEXITCODE)." }
    }
    finally {
        $env:ConnectionStrings__DefaultConnection = $previous
    }
}

switch ($Command) {
    "start" {
        Start-Database
    }
    "stop" {
        Write-Host "Stopping local Postgres (data volume is kept)..." -ForegroundColor Cyan
        Invoke-Compose stop
    }
    "migrate" {
        Start-Database
        Initialize-Database
        Update-Database
    }
    "reset" {
        Write-Host "RESET will permanently delete the local '$database' database (container on 127.0.0.1:$pgPort) and rebuild it from migrations." -ForegroundColor Yellow
        if (-not $Force) {
            $answer = Read-Host "Type '$database' to confirm"
            if ($answer -ne $database) { throw "Reset cancelled." }
        }
        Start-Database
        Invoke-Psql "DROP DATABASE IF EXISTS $database WITH (FORCE)" | Out-Null
        Initialize-Database
        Update-Database
    }
    "connection-string" {
        Write-Output $connectionString
    }
}

if ($Command -ne "connection-string") {
    Write-Host "Done." -ForegroundColor Green
}
