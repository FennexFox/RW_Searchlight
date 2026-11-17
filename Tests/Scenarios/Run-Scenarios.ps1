param([int]$TimeoutSeconds = 420)
$ErrorActionPreference = 'Stop'
$scenarioDir = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$modDir = (Resolve-Path -LiteralPath (Join-Path $scenarioDir '..\..')).Path
$gameDir = (Resolve-Path -LiteralPath (Join-Path $modDir '..\..')).Path
$harnessDir = (Resolve-Path -LiteralPath (Join-Path $scenarioDir 'HarnessMod')).Path
$junction = Join-Path $gameDir 'Mods\SearchlightScenarioHarness'
$exe = Join-Path $gameDir 'RimWorldWin64.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "RimWorld executable missing: $exe" }
if (Test-Path -LiteralPath $junction) { throw "Refusing to replace existing path: $junction" }

dotnet build (Join-Path $scenarioDir 'ScenarioHarness.csproj') -c Release -p:RimWorldDir="$gameDir" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Scenario harness build failed' }

$profile = Join-Path $env:TEMP ('SearchlightScenarios-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $profile 'Config'
New-Item -ItemType Directory -Path $config -Force | Out-Null
$mods = @('brrainz.harmony', 'ludeon.rimworld', 'DirectedSearchlights.Searchlight', 'searchlight.scenarioharness')
$items = ($mods | ForEach-Object { '    <li>' + $_ + '</li>' }) -join "`r`n"
$xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<ModsConfigData>`r`n  <version>1.6</version>`r`n  <activeMods>`r`n$items`r`n  </activeMods>`r`n</ModsConfigData>`r`n"
[IO.File]::WriteAllText((Join-Path $config 'ModsConfig.xml'), $xml, [Text.UTF8Encoding]::new($false))
$createdJunction = $false
$proc = $null
try {
    New-Item -ItemType Junction -Path $junction -Target $harnessDir | Out-Null
    $createdJunction = $true
    $result = Join-Path $profile 'scenario-results.txt'
    $log = Join-Path $profile 'Player.log'
    $argLine = '-quicktest -savedatafolder="' + $profile + '" -ds-scenario-result="' + $result + '" -logFile "' + $log + '" -screen-width 1280 -screen-height 800'
    Write-Output "Profile: $profile"
    Write-Output "Log: $log"
    $proc = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $gameDir -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2; $proc.Refresh() }
    if (-not $proc.HasExited) { $proc.Kill(); throw "Game timeout after $TimeoutSeconds seconds; profile $profile" }
    Write-Output "Exit code: $($proc.ExitCode)"
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    if ($proc.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or
        (Get-Content -LiteralPath $result -Tail 1) -notmatch ' PASS; totalElapsedMs=') {
        throw "Scenario game did not pass; profile $profile"
    }
}
finally {
    if ($createdJunction -and (Test-Path -LiteralPath $junction)) {
        $item = Get-Item -LiteralPath $junction -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or $item.Target -ne $harnessDir) {
            throw "Unexpected junction target; left intact: $junction"
        }
        [IO.Directory]::Delete($junction)
    }
}
