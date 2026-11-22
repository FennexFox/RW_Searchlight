param(
    [switch]$PreciseLighting,
    [int]$TimeoutSeconds = 180,
    [string]$RimWorldPath = (Join-Path $PSScriptRoot '..\..\..\..')
)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $RimWorldPath).Path
$mod = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot '../RunnerModStaging.ps1')
$exe = Join-Path $game 'RimWorld.exe'
if ((Test-Path -LiteralPath $exe -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $game 'Assembly-CSharp.dll') -PathType Leaf)) {
    $gameVersion = '1.7'
}
else {
    $exe = Join-Path $game 'RimWorldWin64.exe'
    if (!(Test-Path -LiteralPath $exe -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $game 'RimWorldWin64_Data/Managed/Assembly-CSharp.dll') -PathType Leaf)) {
        throw "The visual harness requires a RimWorld 1.6 or 1.7 installation: $game"
    }
    $gameVersion = '1.6'
}
$expectedMvid = Get-AssemblyMvid (Join-Path $mod "$gameVersion/Assemblies/DirectedSearchlights.dll")
$profileKind = if ($PreciseLighting) { 'SearchlightVisual-precise-' } else { 'SearchlightVisual-' }
$profile = Join-Path $env:TEMP ($profileKind + [guid]::NewGuid().ToString('N'))
$config = Join-Path $profile 'Config'
$harness = Join-Path $profile 'HarnessMod'
$assemblyDir = Join-Path $harness 'Assemblies'
$about = Join-Path $harness 'About'
New-Item -ItemType Directory -Path $config,$about,$assemblyDir | Out-Null
'<Prefs><runInBackground>True</runInBackground></Prefs>' | Set-Content -LiteralPath (Join-Path $config 'Prefs.xml') -Encoding utf8
@"
<ModMetaData>
  <name>Searchlight Visual Harness</name>
  <author>Searchlight Tests</author>
  <packageId>searchlight.visualharness</packageId>
  <supportedVersions><li>$gameVersion</li></supportedVersions>
</ModMetaData>
"@ | Set-Content -LiteralPath (Join-Path $about 'About.xml') -Encoding utf8
$mods = @('ludeon.rimworld', 'DirectedSearchlights.Searchlight', 'searchlight.visualharness')
if ($PreciseLighting) { $mods = @('brrainz.harmony') + $mods }
$items = ($mods | ForEach-Object { "    <li>$_</li>" }) -join "`n"
@"
<ModsConfigData>
  <version>$gameVersion</version>
  <activeMods>
$items
  </activeMods>
</ModsConfigData>
"@ | Set-Content -LiteralPath (Join-Path $config 'ModsConfig.xml') -Encoding utf8
dotnet build (Join-Path $PSScriptRoot 'VisualHarness.csproj') -c Release "-p:RimWorldDir=$game" "-p:GameVersion=$gameVersion" "-p:OutputPath=$assemblyDir/" "-p:BaseIntermediateOutputPath=$profile/obj/" -v:q
if ($LASTEXITCODE -ne 0) { throw 'Visual harness build failed' }
$junction = Join-Path $game 'Mods/SearchlightVisualHarness'
if (Test-Path -LiteralPath $junction) { throw "Refusing to replace existing path: $junction" }
$createdHarnessJunction = $false
$checkoutJunction = $null
$process = $null
try {
    $checkoutJunction = Add-SearchlightCheckoutToGame $game $mod 'SearchlightVisualMod'
    New-Item -ItemType Junction -Path $junction -Target $harness | Out-Null
    $createdHarnessJunction = $true
    Write-Output "Profile: $profile"
    $argLine = '-quicktest -savedatafolder="' + $profile + '" -ds-visual-output="' + $profile + '"'
    if ($PreciseLighting) { $argLine += ' -ds-visual-precise' }
    if ($gameVersion -eq '1.6') {
        $argLine += ' -logFile "' + (Join-Path $profile 'Player.log') + '" -screen-fullscreen 0 -screen-width 1280 -screen-height 800'
    }
    $process = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $profile 'console.log') -RedirectStandardError (Join-Path $profile 'console-error.log')
    # Keep the OS handle available for ExitCode after a fast game shutdown.
    $null = $process.Handle
    if ($gameVersion -eq '1.7') { & (Join-Path $PSScriptRoot '../Enable-TestRendering.ps1') -GameProcess $process }
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        $process.WaitForExit()
        throw "Visual harness timed out. Profile: $profile"
    }
    $result = Join-Path $profile 'visual-results.txt'
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    $resultText = if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Raw } else { '' }
    if ($process.ExitCode -ne 0 -or $resultText -notmatch '(?m)^PASS\s*$' -or
        $resultText -notmatch ('assemblyMvid=' + [regex]::Escape($expectedMvid) + '(?:\s|;)')) {
        throw "Visual harness failed with exit code $($process.ExitCode). Profile: $profile"
    }
    if ($PreciseLighting) {
        foreach ($scene in '18-tiles','60-tiles','60-tiles-tip','partial-wall','full-wall') {
            foreach ($suffix in '', '-mask') {
                $capture = Get-Item -LiteralPath (Join-Path $profile "$scene$suffix.png")
                if ($capture.Length -le 0) { throw "Empty precise-lighting capture: $capture" }
            }
        }
    }
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($createdHarnessJunction -and (Test-Path -LiteralPath $junction)) {
        $item = Get-Item -LiteralPath $junction -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or $item.Target -ne $harness) {
            throw "Unexpected junction target. Left intact: $junction"
        }
        [IO.Directory]::Delete($junction, $false)
    }
    Remove-SearchlightCheckoutJunction $checkoutJunction
}
