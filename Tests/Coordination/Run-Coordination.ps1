[CmdletBinding()]
param(
    [string]$RimWorldPath = $env:RIMWORLD_DIR,
    [string]$LegacySave,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 420
)
$ErrorActionPreference = 'Stop'
if (!$RimWorldPath) { $RimWorldPath = Join-Path $PSScriptRoot '../../../..' }
$game = (Resolve-Path -LiteralPath $RimWorldPath).Path
$version = Get-Content -LiteralPath (Join-Path $game 'Version.txt') -TotalCount 1
if ($version -notmatch '^(1\.[67])\.') { throw "Expected RimWorld 1.6 or 1.7, found $version" }
$gameVersion = $Matches[1]
if ($LegacySave) { $LegacySave = (Resolve-Path -LiteralPath $LegacySave).Path }
$exeName = if ($gameVersion -eq '1.7') { 'RimWorld.exe' } else { 'RimWorldWin64.exe' }
$exe = Join-Path $game $exeName
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Game executable missing: $exe" }

$profile = Join-Path $env:TEMP ('SearchlightCoordination-' + [guid]::NewGuid().ToString('N'))
$harness = Join-Path $profile 'HarnessMod'
$about = Join-Path $harness 'About'
$assemblies = Join-Path $harness 'Assemblies'
$config = Join-Path $profile 'Config'
$saves = Join-Path $profile 'Saves'
New-Item -ItemType Directory -Path $about,$assemblies,$config,$saves -Force | Out-Null
@"
<ModMetaData>
  <name>Searchlight Coordination Harness</name>
  <author>Searchlight tests</author>
  <packageId>searchlight.coordinationharness</packageId>
  <supportedVersions><li>$gameVersion</li></supportedVersions>
  <loadAfter><li>DirectedSearchlights.Searchlight</li></loadAfter>
</ModMetaData>
"@ | Set-Content -LiteralPath (Join-Path $about 'About.xml') -Encoding utf8
@"
<ModsConfigData>
  <version>$gameVersion</version>
  <activeMods>
    <li>ludeon.rimworld</li>
    <li>DirectedSearchlights.Searchlight</li>
    <li>searchlight.coordinationharness</li>
  </activeMods>
</ModsConfigData>
"@ | Set-Content -LiteralPath (Join-Path $config 'ModsConfig.xml') -Encoding utf8
'<Prefs><devMode>True</devMode><pauseOnLoad>False</pauseOnLoad><runInBackground>True</runInBackground></Prefs>' |
    Set-Content -LiteralPath (Join-Path $config 'Prefs.xml') -Encoding utf8

Write-Output "RimWorld $version coordination profile: $profile"
& dotnet build (Join-Path $PSScriptRoot 'CoordinationHarness.csproj') -c Release "-p:RimWorldDir=$game" `
    "-p:GameVersion=$gameVersion" "-p:LegacyOnly=$([bool]$LegacySave)" "-p:OutputPath=$assemblies/" "-p:BaseIntermediateOutputPath=$profile/obj/" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Coordination harness build failed. Build the matching mod package first.' }

function Invoke-CoordinationGame([string]$Phase) {
    $result = Join-Path $profile "$Phase-results.txt"
    $arguments = '-savedatafolder="' + $profile + '" -ds-coordination-phase=' + $Phase
    if ($Phase -eq 'fresh') { $arguments += ' -quicktest' }
    if ($gameVersion -eq '1.6') {
        $arguments += ' -logFile "' + (Join-Path $profile "$Phase-Player.log") +
            '" -screen-fullscreen 0 -screen-width 1280 -screen-height 800'
    }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $game -WindowStyle Hidden `
        -PassThru -RedirectStandardOutput (Join-Path $profile "$Phase-console.log") `
        -RedirectStandardError (Join-Path $profile "$Phase-console-error.log")
    try {
        $null = $process.Handle
        if ($gameVersion -eq '1.7') {
            & (Join-Path $PSScriptRoot '../Enable-TestRendering.ps1') -GameProcess $process
        }
        $exited = $process.WaitForExit($TimeoutSeconds * 1000)
        if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
        if (!$exited) { throw "Coordination phase $Phase timed out. Profile: $profile" }
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or
            (Get-Content -LiteralPath $result -Tail 1) -ne "PASS $Phase") {
            throw "Coordination phase $Phase failed with exit code $($process.ExitCode). Profile: $profile"
        }
    }
    finally {
        if (!$process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}

$junction = Join-Path $game 'Mods/SearchlightCoordinationHarness'
if (Test-Path -LiteralPath $junction) { throw "Refusing to replace existing path: $junction" }
New-Item -ItemType Junction -Path $junction -Target $harness | Out-Null
try {
    $autostart = Join-Path $saves 'autostart.rws'
    if ($LegacySave) {
        Copy-Item -LiteralPath $LegacySave -Destination $autostart
        Invoke-CoordinationGame 'legacy'
    }
    else {
        Invoke-CoordinationGame 'fresh'
        Copy-Item -LiteralPath (Join-Path $saves 'coordination.rws') -Destination $autostart
        Invoke-CoordinationGame 'current'
    }
}
finally {
    $item = Get-Item -LiteralPath $junction -Force
    if ($item.LinkType -ne 'Junction' -or $item.Target -ne $harness) {
        throw "Unexpected junction target. Left intact: $junction"
    }
    [IO.Directory]::Delete($junction, $false)
}
