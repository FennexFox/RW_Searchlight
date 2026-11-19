param(
    [int]$TimeoutSeconds = 420,
    [string]$RimWorldPath = $env:RIMWORLD_DIR
)
$ErrorActionPreference = 'Stop'
$scenarioDir = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$modDir = (Resolve-Path -LiteralPath (Join-Path $scenarioDir '..\..')).Path
if (!$RimWorldPath) { $RimWorldPath = Join-Path $modDir '..\..' }
$gameDir = (Resolve-Path -LiteralPath $RimWorldPath).Path
$version = Get-Content -LiteralPath (Join-Path $gameDir 'Version.txt') -TotalCount 1
if ($version -notmatch '^(1\.[67])\.') { throw "Expected RimWorld 1.6 or 1.7, found $version" }
$gameVersion = $Matches[1]
$exeName = if ($gameVersion -eq '1.7') { 'RimWorld.exe' } else { 'RimWorldWin64.exe' }
$exe = Join-Path $gameDir $exeName
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "RimWorld executable missing: $exe" }

$profile = Join-Path $env:TEMP ('SearchlightScenarios-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $profile 'Config'
$harnessDir = Join-Path $profile 'HarnessMod'
$about = Join-Path $harnessDir 'About'
$assemblyDir = Join-Path $harnessDir 'Assemblies'
New-Item -ItemType Directory -Path $config,$about,$assemblyDir | Out-Null
'<Prefs><runInBackground>True</runInBackground></Prefs>' | Set-Content -LiteralPath (Join-Path $config 'Prefs.xml') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $scenarioDir 'HarnessMod/About/About.xml') -Destination $about
dotnet build (Join-Path $scenarioDir 'ScenarioHarness.csproj') -c Release "-p:RimWorldDir=$gameDir" "-p:GameVersion=$gameVersion" "-p:OutputPath=$assemblyDir/" "-p:BaseIntermediateOutputPath=$profile/obj/" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Scenario harness build failed' }

$mods = @('ludeon.rimworld', 'DirectedSearchlights.Searchlight', 'searchlight.scenarioharness')
$items = ($mods | ForEach-Object { '    <li>' + $_ + '</li>' }) -join "`r`n"
$xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<ModsConfigData>`r`n  <version>$gameVersion</version>`r`n  <activeMods>`r`n$items`r`n  </activeMods>`r`n</ModsConfigData>`r`n"
[IO.File]::WriteAllText((Join-Path $config 'ModsConfig.xml'), $xml, [Text.UTF8Encoding]::new($false))
$modsDir = (Resolve-Path -LiteralPath (Join-Path $gameDir 'Mods')).Path
$junctions = @{}
$targets = @{ (Join-Path $modsDir 'SearchlightScenarioHarness') = $harnessDir }
$installedMod = Get-Item -LiteralPath (Join-Path $modsDir (Split-Path -Leaf $modDir)) -ErrorAction SilentlyContinue
if ((Split-Path -Parent $modDir) -ne $modsDir -and $installedMod.Target -ne $modDir) {
    $targets[(Join-Path $modsDir 'SearchlightScenarioMod')] = $modDir
}
try {
    foreach ($junction in $targets.Keys) {
        if (Test-Path -LiteralPath $junction) { throw "Refusing to replace existing path: $junction" }
        New-Item -ItemType Junction -Path $junction -Target $targets[$junction] | Out-Null
        $junctions[$junction] = $targets[$junction]
    }
    $result = Join-Path $profile 'scenario-results.txt'
    $log = Join-Path $profile 'console.log'
    $argLine = '-quicktest -savedatafolder="' + $profile + '" -ds-scenario-result="' + $result + '"'
    if ($gameVersion -eq '1.6') {
        $log = Join-Path $profile 'Player.log'
        $argLine += ' -logFile "' + $log + '" -screen-width 1280 -screen-height 800'
    }
    Write-Output "Profile: $profile"
    Write-Output "Log: $log"
    $proc = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $gameDir -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $profile 'console.log') -RedirectStandardError (Join-Path $profile 'console-error.log')
    $null = $proc.Handle
    if ($gameVersion -eq '1.7') {
        & (Join-Path $scenarioDir '../Enable-TestRendering.ps1') -GameProcess $proc
    }
    if (!$proc.WaitForExit($TimeoutSeconds * 1000)) {
        $proc.Kill()
        $proc.WaitForExit()
        throw "Game timeout after $TimeoutSeconds seconds; profile $profile"
    }
    Write-Output "Exit code: $($proc.ExitCode)"
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    if ($proc.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or
        (Get-Content -LiteralPath $result -Tail 1) -notmatch ' PASS; totalElapsedMs=') {
        throw "Scenario game did not pass; profile $profile"
    }
}
finally {
    foreach ($junction in $junctions.Keys) {
        $item = Get-Item -LiteralPath $junction -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) { continue }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or $item.Target -ne $junctions[$junction]) {
            Write-Warning "Unexpected junction target; left intact: $junction"
            continue
        }
        [IO.Directory]::Delete($junction, $false)
    }
}
