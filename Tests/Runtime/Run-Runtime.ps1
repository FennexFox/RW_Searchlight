param(
    [ValidateSet('vanilla','ce','ao','both')][string]$Case = 'vanilla',
    [int]$TimeoutSeconds = 420,
    [switch]$NoReload,
    [switch]$UseVanillaWeather
)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..\..')).Path
$mod = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$harnessSource = Join-Path $PSScriptRoot 'HarnessMod'
$variant = if ($UseVanillaWeather) { "$Case-vanillaweather" } else { $Case }
$testPackage = "searchlight.runtimeharness.$($variant.Replace('-', '.'))"
$link = Join-Path $game "Mods/SearchlightRuntimeHarness-$variant"
$resolvedGame = (Resolve-Path -LiteralPath $game).Path
if (-not $PSScriptRoot.StartsWith($mod + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Harness escaped Searchlight workspace' }
$profile = Join-Path $env:TEMP ('SearchlightRuntime-' + $variant + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$config = Join-Path $profile 'Config'
New-Item -ItemType Directory -Path $config -Force | Out-Null
$harness = Join-Path $profile 'HarnessMod'
$assemblyDir = Join-Path $harness 'Assemblies'
New-Item -ItemType Directory -Path (Join-Path $harness 'About'), $assemblyDir -Force | Out-Null
$about = [xml](Get-Content -LiteralPath (Join-Path $harnessSource 'About/About.xml') -Raw)
$about.ModMetaData.packageId = $testPackage
$about.Save((Join-Path $harness 'About/About.xml'))
if ($UseVanillaWeather) {
    if ($Case -notin 'ao','both') { throw 'The weather engine option requires Atmosphere Overhaul.' }
    # AO 1.6 weatherEngineV1=1 selects its own VanillaDerivation backend.
    $settings = '<SettingsBlock><ModSettings Class="AtmosphereOverhaul.AtmosphereOverhaulSettings"><weatherEngineV1>1</weatherEngineV1></ModSettings></SettingsBlock>'
    [IO.File]::WriteAllText((Join-Path $config 'Mod_3830470974_AtmosphereOverhaulMod.xml'), $settings)
}
$project = Join-Path $PSScriptRoot 'RuntimeHarness.csproj'
$objDir = Join-Path $profile 'obj'
dotnet build $project "-p:RimWorldDir=$resolvedGame" "-p:OutputPath=$assemblyDir\" "-p:BaseIntermediateOutputPath=$objDir\" -v:q
if ($LASTEXITCODE -ne 0) { throw "Harness build failed: $LASTEXITCODE" }
$resolvedHarness = (Resolve-Path -LiteralPath $harness).Path
$mods = @('ludeon.rimworld')
if ($Case -ne 'vanilla') { $mods = @('brrainz.harmony') + $mods }
if ($Case -in 'ce','both') { $mods += 'CETeam.CombatExtended' }
if ($Case -in 'ao','both') { $mods += 'acutus.AtmosphereOverhaul.release' }
$mods += @('DirectedSearchlights.Searchlight', $testPackage)
$items = ($mods | ForEach-Object { '    <li>' + $_ + '</li>' }) -join "`r`n"
$xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<ModsConfigData>`r`n  <version>1.6</version>`r`n  <activeMods>`r`n$items`r`n  </activeMods>`r`n</ModsConfigData>`r`n"
[IO.File]::WriteAllText((Join-Path $config 'ModsConfig.xml'), $xml, [Text.UTF8Encoding]::new($false))
$createdLink = $false
$proc = $null
try {
    if (Test-Path -LiteralPath $link) { throw "Refusing to replace existing path: $link" }
    New-Item -ItemType Junction -Path $link -Target $resolvedHarness | Out-Null
    $createdLink = $true
    $result = Join-Path $profile 'runtime-results.txt'
    $log = Join-Path $profile 'Player.log'
    $exe = Join-Path $resolvedGame 'RimWorldWin64.exe'
    $argLine = '-quicktest -savedatafolder="' + $profile + '" -ds-result="' + $result + '" -logFile "' + $log + '" -screen-width 1280 -screen-height 800'
    if ($NoReload) { $argLine += ' -ds-no-reload' }
    Write-Output "Profile: $profile"
    Write-Output "Mods: $($mods -join ', ')"
    $proc = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $resolvedGame -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2; $proc.Refresh() }
    if (-not $proc.HasExited) { $proc.Kill(); throw "Game timeout after $TimeoutSeconds seconds; profile $profile" }
    Write-Output "Exit code: $($proc.ExitCode)"
    if (Test-Path $result) { Get-Content $result }
    if (Test-Path $log) { Write-Output "Log: $log" }
    if ($proc.ExitCode -ne 0) { throw "Game exited $($proc.ExitCode); profile $profile" }
    if (-not (Test-Path $result) -or -not ((Get-Content $result -Tail 1) -match ' PASS;')) { throw "Harness did not report PASS; profile $profile" }
}
finally {
    if ($createdLink -and (Test-Path -LiteralPath $link)) {
        $item = Get-Item -LiteralPath $link -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or $item.Target -ne $resolvedHarness) { throw "Unexpected harness link target; left intact: $link" }
        [IO.Directory]::Delete($link, $false)
    }
}
