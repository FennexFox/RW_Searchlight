[CmdletBinding()]
param([string] $RimWorldPath = $env:RIMWORLD_DIR)
$ErrorActionPreference = 'Stop'
if (-not $RimWorldPath) {
    $RimWorldPath = Join-Path $PSScriptRoot '../..'
}
$game = (Resolve-Path -LiteralPath $RimWorldPath).Path
$managed = Join-Path $game 'RimWorldWin64_Data/Managed'
if (-not (Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll'))) {
    throw "Set -RimWorldPath to the RimWorld installation containing RimWorldWin64_Data/Managed."
}
$version = Get-Content -LiteralPath (Join-Path $game 'Version.txt') -TotalCount 1
if ($version -notmatch '^1\.6\.') { throw "Expected RimWorld 1.6, found $version" }
Write-Host "Building against RimWorld $version"
& dotnet build (Join-Path $PSScriptRoot 'Source/DirectedSearchlights.csproj') -c Release "-p:RimWorldDir=$game" --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
