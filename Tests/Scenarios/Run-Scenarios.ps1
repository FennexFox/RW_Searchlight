param(
    [int]$TimeoutSeconds = 420,
    [string]$RimWorldPath = $env:RIMWORLD_DIR
)
$ErrorActionPreference = 'Stop'
$scenarioDir = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$modDir = (Resolve-Path -LiteralPath (Join-Path $scenarioDir '..\..')).Path
. (Join-Path $scenarioDir '../RunnerModStaging.ps1')
if (!$RimWorldPath) { $RimWorldPath = Join-Path $modDir '..\..' }
$gameDir = (Resolve-Path -LiteralPath $RimWorldPath).Path
$version = Get-Content -LiteralPath (Join-Path $gameDir 'Version.txt') -TotalCount 1
if ($version -notmatch '^(1\.[67])\.') { throw "Expected RimWorld 1.6 or 1.7, found $version" }
$gameVersion = $Matches[1]
$exeName = if ($gameVersion -eq '1.7') { 'RimWorld.exe' } else { 'RimWorldWin64.exe' }
$exe = Join-Path $gameDir $exeName
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "RimWorld executable missing: $exe" }
$expectedMvid = Get-AssemblyMvid (Join-Path $modDir "$gameVersion/Assemblies/DirectedSearchlights.dll")

$profile = Join-Path $env:TEMP ('SearchlightScenarios-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $profile 'Config'
$saves = Join-Path $profile 'Saves'
$harnessDir = Join-Path $profile 'HarnessMod'
$about = Join-Path $harnessDir 'About'
$assemblyDir = Join-Path $harnessDir 'Assemblies'
New-Item -ItemType Directory -Path $config,$saves,$about,$assemblyDir | Out-Null
'<Prefs><devMode>True</devMode><pauseOnLoad>False</pauseOnLoad><runInBackground>True</runInBackground></Prefs>' |
    Set-Content -LiteralPath (Join-Path $config 'Prefs.xml') -Encoding utf8
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
$checkoutJunction = $null
try {
    $checkoutJunction = Add-SearchlightCheckoutToGame $gameDir $modDir 'SearchlightScenarioMod'
    foreach ($junction in $targets.Keys) {
        if (Test-Path -LiteralPath $junction) { throw "Refusing to replace existing path: $junction" }
        New-Item -ItemType Junction -Path $junction -Target $targets[$junction] | Out-Null
        $junctions[$junction] = $targets[$junction]
    }
    $result = Join-Path $profile 'scenario-results.txt'
    Write-Output "Profile: $profile"
    foreach ($phase in @('fresh', 'current')) {
        $log = Join-Path $profile "$phase-console.log"
        $argLine = '-savedatafolder="' + $profile + '" -ds-scenario-phase=' + $phase +
            ' -ds-scenario-result="' + $result + '"'
        if ($phase -eq 'fresh') { $argLine = '-quicktest ' + $argLine }
        if ($gameVersion -eq '1.6') {
            $log = Join-Path $profile "$phase-Player.log"
            $argLine += ' -logFile "' + $log + '" -screen-width 1280 -screen-height 800'
        }
        Write-Output "$phase log: $log"
        $resultLineCount = if (Test-Path -LiteralPath $result) { @(Get-Content -LiteralPath $result).Count } else { 0 }
        $proc = $null
        try {
            $proc = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $gameDir -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $profile "$phase-console.log") `
                -RedirectStandardError (Join-Path $profile "$phase-console-error.log")
            $null = $proc.Handle
            if ($gameVersion -eq '1.7') {
                & (Join-Path $scenarioDir '../Enable-TestRendering.ps1') -GameProcess $proc
            }
            if (!$proc.WaitForExit($TimeoutSeconds * 1000)) {
                throw "Scenario $phase phase timed out after $TimeoutSeconds seconds; profile $profile"
            }
            Write-Output "$phase exit code: $($proc.ExitCode)"
            $phaseResult = if (Test-Path -LiteralPath $result) {
                (@(Get-Content -LiteralPath $result) | Select-Object -Skip $resultLineCount) -join [Environment]::NewLine
            }
            else { '' }
            if ($proc.ExitCode -ne 0) {
                $failure = [regex]::Match($phaseResult, '(?m)^\S+ FAIL\b[^\r\n]*').Value
                if (!$failure) { $failure = "See game log: $log" }
                throw "Scenario $phase phase exited with code $($proc.ExitCode). $failure (profile $profile)"
            }
            if ($phaseResult -notmatch ('assemblyMvid=' + [regex]::Escape($expectedMvid) + '(?:\s|;)')) {
                throw "Scenario $phase phase did not use this checkout's assembly; profile $profile"
            }
            if ($phase -eq 'fresh') {
                $save = Join-Path $saves 'night-only.rws'
                if ($phaseResult -notmatch 'night-only SAVE' -or !(Test-Path -LiteralPath $save)) {
                    throw "Scenario fresh phase did not create the night-only save; profile $profile"
                }
                Copy-Item -LiteralPath $save -Destination (Join-Path $saves 'autostart.rws')
            }
            elseif ($phaseResult -notmatch ' PASS; totalElapsedMs=') {
                throw "Scenario game did not pass after reload; profile $profile"
            }
        }
        finally {
            if ($null -ne $proc) {
                if (!$proc.HasExited) {
                    $proc.Kill()
                    $proc.WaitForExit()
                }
                $proc.Dispose()
            }
        }
    }
    Get-Content -LiteralPath $result
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
    Remove-SearchlightCheckoutJunction $checkoutJunction
}
