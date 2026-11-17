[CmdletBinding()]
param([string] $RimWorldPath = $env:RIMWORLD_DIR)
$ErrorActionPreference = 'Stop'
if (-not $RimWorldPath) {
    $RimWorldPath = Join-Path $PSScriptRoot '../..'
}
$game = (Resolve-Path -LiteralPath $RimWorldPath).Path
$version = Get-Content -LiteralPath (Join-Path $game 'Version.txt') -TotalCount 1
if ($version -notmatch '^(1\.[67])\.') { throw "Expected RimWorld 1.6 or 1.7, found $version" }
$gameVersion = $Matches[1]
$assembly = if ($gameVersion -eq '1.7') { Join-Path $game 'Assembly-CSharp.dll' } else { Join-Path $game 'RimWorldWin64_Data/Managed/Assembly-CSharp.dll' }
if (-not (Test-Path -LiteralPath $assembly)) { throw "Game assembly missing: $assembly" }
Write-Host "Building against RimWorld $version"
& dotnet build (Join-Path $PSScriptRoot 'Source/DirectedSearchlights.csproj') -c Release "-p:RimWorldDir=$game" "-p:GameVersion=$gameVersion" --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
