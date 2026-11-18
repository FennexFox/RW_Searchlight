param([int]$TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..\..')).Path
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
$profile = Join-Path $env:TEMP ('SearchlightVisual-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $profile 'Config'
$harness = Join-Path $profile 'HarnessMod'
$assemblyDir = Join-Path $harness 'Assemblies'
$about = Join-Path $harness 'About'
New-Item -ItemType Directory -Path $config,$about,$assemblyDir | Out-Null
@"
<ModMetaData>
  <name>Searchlight Visual Harness</name>
  <author>Searchlight Tests</author>
  <packageId>searchlight.visualharness</packageId>
  <supportedVersions><li>$gameVersion</li></supportedVersions>
</ModMetaData>
"@ | Set-Content -LiteralPath (Join-Path $about 'About.xml') -Encoding utf8
@"
<ModsConfigData>
  <version>$gameVersion</version>
  <activeMods>
    <li>ludeon.rimworld</li>
    <li>DirectedSearchlights.Searchlight</li>
    <li>searchlight.visualharness</li>
  </activeMods>
</ModsConfigData>
"@ | Set-Content -LiteralPath (Join-Path $config 'ModsConfig.xml') -Encoding utf8
dotnet build (Join-Path $PSScriptRoot 'VisualHarness.csproj') -c Release "-p:RimWorldDir=$game" "-p:GameVersion=$gameVersion" "-p:OutputPath=$assemblyDir/" "-p:BaseIntermediateOutputPath=$profile/obj/" -v:q
if ($LASTEXITCODE -ne 0) { throw 'Visual harness build failed' }
$junction = Join-Path $game 'Mods/SearchlightVisualHarness'
if (Test-Path -LiteralPath $junction) { throw "Refusing to replace existing path: $junction" }
New-Item -ItemType Junction -Path $junction -Target $harness | Out-Null
try {
    Write-Output "Profile: $profile"
    $argLine = '-quicktest -savedatafolder="' + $profile + '" -ds-visual-output="' + $profile + '"'
    if ($gameVersion -eq '1.6') {
        $argLine += ' -logFile "' + (Join-Path $profile 'Player.log') + '" -screen-fullscreen 0 -screen-width 1280 -screen-height 800'
    }
    $process = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $profile 'console.log') -RedirectStandardError (Join-Path $profile 'console-error.log')
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        $process.WaitForExit()
        throw "Visual harness timed out. Profile: $profile"
    }
    $result = Join-Path $profile 'visual-results.txt'
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or (Get-Content -LiteralPath $result -Tail 1) -ne 'PASS') {
        throw "Visual harness failed with exit code $($process.ExitCode). Profile: $profile"
    }
}
finally {
    $item = Get-Item -LiteralPath $junction -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or $item.Target -ne $harness) {
        throw "Unexpected junction target. Left intact: $junction"
    }
    [IO.Directory]::Delete($junction, $false)
}
