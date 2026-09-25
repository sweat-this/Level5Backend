<#
.SYNOPSIS
    Deterministic local E2E fixture workflow against the level5_v2_e2e database (see db.ps1).

.DESCRIPTION
    e2e-seed <scenario>   DESTRUCTIVE. Resets level5_v2_e2e (db.ps1 e2e-reset), then seeds it with
                          the named deterministic scenario via Level5.E2E.Fixtures. Always resets
                          first - level5_v2_e2e is meant to be reset freely and must never
                          accumulate leftover data between seed runs, so every call to this command
                          starts from an empty, freshly-migrated database.

    Known scenarios: baseline, friends, pending-friend, challenge-invited, series-active,
    series-one-attempt-complete, series-completed, challenge-declined, challenge-cancelled,
    challenge-expired.

    Level5.E2E.Fixtures itself refuses to run against any database except the literal
    "level5_v2_e2e" (a hardcoded safety rail inside the tool, not something this script can
    override), and never accepts ConnectionStrings__DefaultConnection from the caller - it is
    always set here, from db.ps1's own e2e connection-string resolution.

.EXAMPLE
    ./v2/scripts/e2e.ps1 e2e-seed baseline

.EXAMPLE
    ./v2/scripts/e2e.ps1 e2e-seed series-completed -Force
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("e2e-seed")]
    [string]$Command,

    [Parameter(Mandatory = $true, Position = 1)]
    [string]$Scenario,

    # Skips the interactive confirmation on db.ps1's underlying e2e-reset.
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$v2Root = Split-Path $scriptDir -Parent

$dbScript = Join-Path $scriptDir "db.ps1"
$fixturesProject = Join-Path $v2Root "tools/Level5.E2E.Fixtures/Level5.E2E.Fixtures.csproj"

Write-Host "Resetting level5_v2_e2e..." -ForegroundColor Cyan
if ($Force) {
    & $dbScript e2e-reset -Force
} else {
    & $dbScript e2e-reset
}
if ($LASTEXITCODE -ne 0) { throw "db.ps1 e2e-reset failed (exit code $LASTEXITCODE)." }

$connectionString = & $dbScript connection-string
# db.ps1's connection-string command always reports the level5_v2 dev database name - swap in the
# e2e database name explicitly rather than trusting any part of this to come from elsewhere, since
# Level5.E2E.Fixtures' own safety rail checks this exact value.
$connectionString = $connectionString -replace "Database=level5_v2;", "Database=level5_v2_e2e;"

Write-Host "Seeding scenario '$Scenario' into level5_v2_e2e..." -ForegroundColor Cyan
$previous = $env:ConnectionStrings__DefaultConnection
try {
    $env:ConnectionStrings__DefaultConnection = $connectionString
    dotnet run --project $fixturesProject -- seed $Scenario
    if ($LASTEXITCODE -ne 0) { throw "Level5.E2E.Fixtures failed (exit code $LASTEXITCODE)." }
}
finally {
    $env:ConnectionStrings__DefaultConnection = $previous
}

Write-Host "Done." -ForegroundColor Green
