[CmdletBinding()]
param(
    [ValidateSet('vanilla','ce')][string]$Case = 'vanilla',
    [ValidateSet('legacy','precise')][string]$Mode,
    [switch]$Lifecycle,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 240,
    [string]$RimWorldPath = $env:RIMWORLD_DIR
)
$ErrorActionPreference = 'Stop'
if ($Mode -and $Lifecycle) { throw 'Select either -Mode or -Lifecycle for a single fixture run.' }
if (!$RimWorldPath) { $RimWorldPath = Join-Path $PSScriptRoot '../../../..' }
$game = (Resolve-Path -LiteralPath $RimWorldPath).Path
$mod = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot '../RunnerModStaging.ps1')

$version = Get-Content -LiteralPath (Join-Path $game 'Version.txt') -TotalCount 1
if ($version -notmatch '^(1\.[67])\.') { throw "Expected RimWorld 1.6 or 1.7, found $version" }
$gameVersion = $Matches[1]
$exeName = if ($gameVersion -eq '1.7') { 'RimWorld.exe' } else { 'RimWorldWin64.exe' }
$exe = Join-Path $game $exeName
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Game executable missing: $exe" }
$expectedMvid = Get-AssemblyMvid (Join-Path $mod "$gameVersion/Assemblies/DirectedSearchlights.dll")

$fixtureId = [guid]::NewGuid().ToString('N')
$variant = if ($Lifecycle) { "$Case-lifecycle" } elseif ($Mode) { "$Case-$Mode" } else { $Case }
$profile = Join-Path $env:TEMP "SearchlightLighting-$variant-$fixtureId"
$config = Join-Path $profile 'Config'
$harness = Join-Path $profile 'HarnessMod'
$assemblies = Join-Path $harness 'Assemblies'
$about = Join-Path $harness 'About'
$saves = Join-Path $profile 'Saves'
New-Item -ItemType Directory -Path $config,$about,$assemblies,$saves | Out-Null
# RimWorld only loads autostart.rws when developer mode is enabled.
'<Prefs><devMode>True</devMode><pauseOnLoad>False</pauseOnLoad><runInBackground>True</runInBackground></Prefs>' |
    Set-Content -LiteralPath (Join-Path $config 'Prefs.xml') -Encoding utf8
$package = 'searchlight.lightingharness'
@"
<ModMetaData>
  <name>Searchlight Lighting Harness</name>
  <author>Searchlight Tests</author>
  <packageId>$package</packageId>
  <supportedVersions><li>$gameVersion</li></supportedVersions>
</ModMetaData>
"@ | Set-Content -LiteralPath (Join-Path $about 'About.xml') -Encoding utf8
$mods = @('brrainz.harmony', 'ludeon.rimworld')
if ($Case -eq 'ce') { $mods += 'CETeam.CombatExtended' }
$mods += @('DirectedSearchlights.Searchlight', $package)
$items = ($mods | ForEach-Object { "    <li>$_</li>" }) -join "`n"
@"
<ModsConfigData>
  <version>$gameVersion</version>
  <activeMods>
$items
  </activeMods>
</ModsConfigData>
"@ | Set-Content -LiteralPath (Join-Path $config 'ModsConfig.xml') -Encoding utf8

Write-Output "RimWorld $version lighting profile: $profile"
Write-Output "Mods: $($mods -join ', ')"
& dotnet build (Join-Path $PSScriptRoot 'LightingHarness.csproj') -c Release "-p:RimWorldDir=$game" `
    "-p:GameVersion=$gameVersion" "-p:OutputPath=$assemblies/" "-p:BaseIntermediateOutputPath=$profile/obj/" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Lighting harness build failed. Build the matching mod package first.' }

function Invoke-LightingGame([string]$Phase) {
    $result = Join-Path $profile "$Phase-results.txt"
    $fixtureArgs = if ($Lifecycle) { 'ds-lifecycle' } else { 'ds-lighting' }
    $arguments = '-savedatafolder="' + $profile + '" -' + $fixtureArgs + '-output="' + $result + '" -' + $fixtureArgs + '-phase=' + $Phase
    if ($Phase -eq 'fresh') { $arguments += ' -quicktest' }
    if ($Case -eq 'ce') { $arguments += ' -ds-lighting-require-ce' }
    if ($Mode) { $arguments += ' -ds-lighting-mode=' + $Mode }
    if ($gameVersion -eq '1.6') {
        $arguments += ' -logFile "' + (Join-Path $profile "$Phase-Player.log") +
            '" -screen-fullscreen 0 -screen-width 1280 -screen-height 800'
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $game -WindowStyle Hidden `
        -PassThru -RedirectStandardOutput (Join-Path $profile "$Phase-console.log") `
        -RedirectStandardError (Join-Path $profile "$Phase-console-error.log")
    try {
        $null = $process.Handle
        if ($gameVersion -eq '1.7') {
            & (Join-Path $PSScriptRoot '../Enable-TestRendering.ps1') -GameProcess $process -Deadline $deadline
        }
        $remaining = [int][Math]::Max(0, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        $exited = $process.WaitForExit($remaining)
        $resultText = if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Raw } else { '' }
        if ($resultText) { Write-Output $resultText.TrimEnd() }
        if (!$exited) { throw "Lighting phase $Phase timed out. Profile: $profile" }
        if ($process.ExitCode -ne 0 -or $resultText -notmatch "(?m)^PASS $Phase\s*$" -or
            $resultText -notmatch ('assemblyMvid=' + [regex]::Escape($expectedMvid) + '(?:\s|;)')) {
            throw "Lighting phase $Phase failed or loaded a different mod DLL. Exit code $($process.ExitCode). Profile: $profile"
        }
    }
    finally {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}

$link = Join-Path $game "Mods/SearchlightLightingHarness-$fixtureId"
$checkoutJunction = $null
$harnessJunction = $null
try {
    $checkoutJunction = Add-SearchlightCheckoutToGame $game $mod "SearchlightLightingMod-$fixtureId"
    New-Item -ItemType Junction -Path $link -Target $harness | Out-Null
    $harnessJunction = @{ Path = $link; Target = $harness }
    Invoke-LightingGame 'fresh'
    $autostart = Join-Path $saves 'autostart.rws'
    if ($Lifecycle) {
        $migrationSave = New-Object System.Xml.XmlDocument
        $migrationSave.PreserveWhitespace = $true
        $migrationSave.Load((Join-Path $saves 'searchlight-lighting-lifecycle.rws'))
        $modeComponents = $migrationSave.SelectNodes("//game/components/li[@Class='DirectedSearchlights.SearchlightLightingGame']")
        if ($modeComponents.Count -ne 1) { throw "Expected one lighting mode component in the isolated fixture. Profile: $profile" }
        $modeComponent = $modeComponents.Item(0)
        [void]$modeComponent.ParentNode.RemoveChild($modeComponent)
        $migrationSave.Save($autostart)
    }
    else {
        Copy-Item -LiteralPath (Join-Path $saves 'searchlight-lighting.rws') -Destination $autostart
    }
    Invoke-LightingGame 'reload'
}
finally {
    try { Remove-SearchlightCheckoutJunction $harnessJunction }
    finally { Remove-SearchlightCheckoutJunction $checkoutJunction }
}
