[CmdletBinding()]
param(
    [string]$RimWorldPath = $env:RIMWORLD_DIR,
    [string]$HarmonyPath,
    [string]$Seed = 'SearchlightPerf',
    [switch]$Full,
    [ValidateRange(1, 100000)][int]$WarmupTicks,
    [ValidateRange(10, 1000000)][int]$SampleTicks,
    [ValidateRange(1, 20)][int]$Repeats,
    [string]$Counts,
    [string]$Workloads,
    [ValidateRange(60, 7200)][int]$TimeoutSeconds = 7200
)
$ErrorActionPreference = 'Stop'
if ($Seed -notmatch '^[A-Za-z0-9_-]{1,64}$') { throw 'Seed must contain 1-64 letters, digits, underscores or hyphens.' }
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

if ($Full) {
    if (!$PSBoundParameters.ContainsKey('WarmupTicks')) { $WarmupTicks = 600 }
    if (!$PSBoundParameters.ContainsKey('SampleTicks')) { $SampleTicks = 3600 }
    if (!$PSBoundParameters.ContainsKey('Repeats')) { $Repeats = 3 }
    if (!$PSBoundParameters.ContainsKey('Counts')) { $Counts = '0,10,50' }
    if (!$PSBoundParameters.ContainsKey('Workloads')) { $Workloads = 'Static,Rotating,MovingTarget,ToggledDoor' }
} else {
    if (!$PSBoundParameters.ContainsKey('WarmupTicks')) { $WarmupTicks = 30 }
    if (!$PSBoundParameters.ContainsKey('SampleTicks')) { $SampleTicks = 120 }
    if (!$PSBoundParameters.ContainsKey('Repeats')) { $Repeats = 1 }
    if (!$PSBoundParameters.ContainsKey('Counts')) { $Counts = '0,1' }
    if (!$PSBoundParameters.ContainsKey('Workloads')) { $Workloads = 'Static' }
}

$steamapps = $game
while ($steamapps -and (Split-Path -Leaf $steamapps) -ne 'steamapps') {
    $parent = Split-Path -Parent $steamapps
    if (!$parent -or $parent -eq $steamapps) { $steamapps = $null; break }
    $steamapps = $parent
}
$harmonyCandidates = @(
    (Join-Path $game 'Mods/Harmony/Current/Assemblies/0Harmony.dll'),
    (Join-Path $game 'Mods/Harmony/Assemblies/0Harmony.dll')
)
if ($steamapps) { $harmonyCandidates += Join-Path $steamapps "workshop/content/294100/2009463077/$gameVersion/Assemblies/0Harmony.dll" }
$harmonyAssembly = if ($HarmonyPath) { (Resolve-Path -LiteralPath $HarmonyPath).Path } else {
    $harmonyCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (!$harmonyAssembly) { throw "Harmony is required for whole-tick instrumentation. Install brrainz.harmony in $game/Mods." }
$harmonyPackage = 'brrainz.harmony'

$profile = Join-Path $env:TEMP ('SearchlightLightingPerformance-' + [guid]::NewGuid().ToString('N'))
$harness = Join-Path $profile 'HarnessMod'
$config = Join-Path $profile 'Config'
$assemblies = Join-Path $harness 'Assemblies'
New-Item -ItemType Directory -Path (Join-Path $harness 'About'),$assemblies,$config,(Join-Path $profile 'Saves') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'HarnessMod/About/About.xml') -Destination (Join-Path $harness 'About/About.xml')
$mods = @($harmonyPackage, 'ludeon.rimworld', 'DirectedSearchlights.Searchlight', 'searchlight.lightingperformanceharness')
$items = ($mods | ForEach-Object { '    <li>' + $_ + '</li>' }) -join "`r`n"
"<ModsConfigData><version>$gameVersion</version><activeMods>`r`n$items`r`n</activeMods></ModsConfigData>" |
    Set-Content -LiteralPath (Join-Path $config 'ModsConfig.xml') -Encoding utf8
'<Prefs><devMode>True</devMode><pauseOnLoad>False</pauseOnLoad><runInBackground>True</runInBackground></Prefs>' |
    Set-Content -LiteralPath (Join-Path $config 'Prefs.xml') -Encoding utf8
$expectedMvid = Get-AssemblyMvid (Join-Path $mod "$gameVersion/Assemblies/DirectedSearchlights.dll")
$result = Join-Path $profile 'lighting-performance.txt'
$junction = Join-Path $game 'Mods/SearchlightLightingPerformanceHarness'
$checkoutJunction = $null
$createdHarnessJunction = $false
$process = $null
try {
    $checkoutJunction = Add-SearchlightCheckoutToGame $game $mod 'SearchlightLightingPerformanceMod'
    if (Test-Path -LiteralPath $junction) { throw "Refusing to replace existing path: $junction" }
    New-Item -ItemType Junction -Path $junction -Target $harness | Out-Null
    $createdHarnessJunction = $true
    & dotnet build (Join-Path $PSScriptRoot 'LightingPerformanceHarness.csproj') -c Release `
        "-p:RimWorldDir=$game" "-p:GameVersion=$gameVersion" "-p:HarmonyAssemblyPath=$harmonyAssembly" `
        "-p:OutputPath=$assemblies/" "-p:BaseIntermediateOutputPath=$profile/obj/" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Lighting performance harness build failed.' }

    $log = Join-Path $profile 'Player.log'
    $arguments = '-quicktest -ds-lighting-seed="' + $Seed + '" -savedatafolder="' + $profile + '" -ds-lighting-performance-result="' + $result + '"' +
        ' -ds-lighting-warmup=' + $WarmupTicks + ' -ds-lighting-sample=' + $SampleTicks +
        ' -ds-lighting-repeats=' + $Repeats + ' -ds-lighting-counts=' + $Counts + ' -ds-lighting-workloads=' + $Workloads
    if ($gameVersion -eq '1.6') { $arguments += ' -logFile "' + $log + '" -screen-fullscreen 0 -screen-width 1280 -screen-height 800' }
    Write-Output "Profile: $profile"
    Write-Output "RimWorld: $version; seed=$Seed; mods: $($mods -join ', '); counts=$Counts; workloads=$Workloads; warmup=$WarmupTicks; sample=$SampleTicks; repeats=$Repeats"
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $profile 'console.log') -RedirectStandardError (Join-Path $profile 'console-error.log')
    $null = $process.Handle
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    if ($gameVersion -eq '1.7') { & (Join-Path $PSScriptRoot '../Enable-TestRendering.ps1') -GameProcess $process -Deadline $deadline }
    $remaining = [int][Math]::Max(0, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
    if (!$process.WaitForExit($remaining)) { throw "Performance run timed out. Profile: $profile" }
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    if ($process.ExitCode -ne 0) { throw "Performance harness exited $($process.ExitCode). Profile: $profile" }
    $text = if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Raw } else { '' }
    if ($text -notmatch '(?m) PASS;' -or $text -notmatch ('assemblyMvid=' + [regex]::Escape($expectedMvid))) {
        throw "Performance harness did not report PASS with the packaged checkout assembly. Profile: $profile"
    }
    $requiredWorkloads = @('Static', 'Rotating', 'MovingTarget', 'ToggledDoor')
    $enforce = $Full -and $WarmupTicks -ge 600 -and $SampleTicks -ge 3600 -and
        $Repeats -ge 3 -and '10' -in $Counts.Split(',') -and
        @($requiredWorkloads | Where-Object { $_ -notin $Workloads.Split(',') }).Count -eq 0
    & (Join-Path $PSScriptRoot 'Summarize-LightingPerformance.ps1') -ResultPath $result -EnforceGate:$enforce
}
finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($createdHarnessJunction -and (Test-Path -LiteralPath $junction)) {
        $item = Get-Item -LiteralPath $junction -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or $item.Target -ne $harness) {
            throw "Unexpected harness junction target; left in place: $junction"
        }
        [IO.Directory]::Delete($junction, $false)
    }
    Remove-SearchlightCheckoutJunction $checkoutJunction
}
