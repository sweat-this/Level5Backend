<#
.SYNOPSIS
    Builds a self-contained EF Core migration bundle for Backend V2.

.DESCRIPTION
    Uses Level5.Infrastructure as the migrations project and Level5.Api as the startup project
    (the same Level5V2DbContextFactory design-time factory db.ps1 and "Adding a migration" use -
    see v2/README.md), via the repo-pinned local dotnet-ef tool (run `dotnet tool restore` once
    from the repository root first - see ../../.config/dotnet-tools.json).

    This script only compiles the bundle executable. It never connects to or modifies a database:
    the bundle reads ConnectionStrings__DefaultConnection at *its own* run time, from whatever
    environment executes it later - never from this script, and never from any value set here.

    Running the produced bundle applies migrations and requires the migration-role connection
    string (see v2/README.md's "Deployment and migration runbook"); that step is deliberately not
    part of this script.

.PARAMETER OutputPath
    Where to write the bundle executable. Defaults to v2/artifacts/efbundle (gitignored).

.PARAMETER Runtime
    Target self-contained runtime identifier. Defaults to linux-x64 (the deploy target). Override
    with e.g. win-x64 to produce a bundle runnable on a local Windows machine.

.EXAMPLE
    ./v2/scripts/build-migration-bundle.ps1

.EXAMPLE
    ./v2/scripts/build-migration-bundle.ps1 -Runtime win-x64 -OutputPath ./efbundle
#>
param(
    [string]$OutputPath,
    [string]$Runtime = "linux-x64"
)

$ErrorActionPreference = "Stop"
$v2Root = Split-Path $PSScriptRoot -Parent

if (-not $OutputPath) {
    $OutputPath = Join-Path $v2Root "artifacts/efbundle"
}

Write-Host "Building V2 EF Core migration bundle ($Runtime) at $OutputPath..." -ForegroundColor Cyan

# Level5V2DbContextFactory (the design-time factory `dotnet ef` uses) requires
# ConnectionStrings__DefaultConnection to be set just to construct a DbContext instance and read
# the compiled migrations - EF/Npgsql never actually opens a connection for that, so this
# placeholder value is never dialed. Scoped to this call only, exactly like db.ps1's
# Update-Database, and restored afterward so it can never leak into an unrelated command.
$previousConnectionString = $env:ConnectionStrings__DefaultConnection
try {
    $env:ConnectionStrings__DefaultConnection = "Host=unused;Database=unused;Username=unused;Password=unused"
    dotnet ef migrations bundle `
        --project (Join-Path $v2Root "src/Level5.Infrastructure") `
        --startup-project (Join-Path $v2Root "src/Level5.Api") `
        --self-contained `
        -r $Runtime `
        -o $OutputPath `
        --force
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef migrations bundle failed (exit code $LASTEXITCODE)." }
}
finally {
    $env:ConnectionStrings__DefaultConnection = $previousConnectionString
}

Write-Host "Done. Bundle written to $OutputPath." -ForegroundColor Green
Write-Host "Run it with ConnectionStrings__DefaultConnection set to the migration-role connection string - see v2/README.md's deployment runbook. It never runs automatically as part of this script or the API image." -ForegroundColor Green
