param(
    [ValidateSet('vanilla','ce','ao','both')][string]$Case = 'vanilla',
    [int]$TimeoutSeconds = 420,
    [switch]$NoReload,
    [switch]$UseVanillaWeather,
    [string]$RimWorldDir = (Join-Path $PSScriptRoot '..\..\..\..')
)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $RimWorldDir).Path
$exe = Join-Path $game 'RimWorld.exe'
if ((Test-Path -LiteralPath $exe -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $game 'Assembly-CSharp.dll') -PathType Leaf)) {
    $gameVersion = '1.7'
} else {
    $exe = Join-Path $game 'RimWorldWin64.exe'
    if (!(Test-Path -LiteralPath $exe -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $game 'RimWorldWin64_Data/Managed/Assembly-CSharp.dll') -PathType Leaf)) {
        throw "The runtime harness requires a RimWorld 1.6 or 1.7 installation: $game"
    }
    $gameVersion = '1.6'
}
$mod = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$harnessSource = Join-Path $PSScriptRoot 'HarnessMod'
$variant = if ($UseVanillaWeather) { "$Case-vanillaweather" } else { $Case }
$testPackage = "searchlight.runtimeharness.$($variant.Replace('-', '.'))"
$link = Join-Path $game "Mods/SearchlightRuntimeHarness-$variant"
if (-not $PSScriptRoot.StartsWith($mod + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Harness escaped Searchlight workspace' }
$profile = Join-Path $env:TEMP ('SearchlightRuntime-' + $variant + '-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $profile 'Config'
New-Item -ItemType Directory -Path $config -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $config 'Prefs.xml'), '<PrefsData><runInBackground>true</runInBackground></PrefsData>')
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
dotnet build $project "-p:RimWorldDir=$game" "-p:GameVersion=$gameVersion" "-p:OutputPath=$assemblyDir/" "-p:BaseIntermediateOutputPath=$objDir/" -v:q
if ($LASTEXITCODE -ne 0) { throw "Harness build failed: $LASTEXITCODE" }
$resolvedHarness = (Resolve-Path -LiteralPath $harness).Path
$mods = @('ludeon.rimworld')
if ($Case -ne 'vanilla') { $mods = @('brrainz.harmony') + $mods }
if ($Case -in 'ce','both') { $mods += 'CETeam.CombatExtended' }
if ($Case -in 'ao','both') { $mods += 'acutus.AtmosphereOverhaul.release' }
$mods += @('DirectedSearchlights.Searchlight', $testPackage)
$items = ($mods | ForEach-Object { '    <li>' + $_ + '</li>' }) -join "`r`n"
$xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<ModsConfigData>`r`n  <version>$gameVersion</version>`r`n  <activeMods>`r`n$items`r`n  </activeMods>`r`n</ModsConfigData>`r`n"
[IO.File]::WriteAllText((Join-Path $config 'ModsConfig.xml'), $xml, [Text.UTF8Encoding]::new($false))
$createdLink = $false
$proc = $null
try {
    if (Test-Path -LiteralPath $link) { throw "Refusing to replace existing path: $link" }
    New-Item -ItemType Junction -Path $link -Target $resolvedHarness | Out-Null
    $createdLink = $true
    $result = Join-Path $profile 'runtime-results.txt'
    $log = Join-Path $profile 'console.log'
    $argLine = '-quicktest -savedatafolder="' + $profile + '" -ds-result="' + $result + '"'
    if ($gameVersion -eq '1.6') {
        $log = Join-Path $profile 'Player.log'
        $argLine += ' -logFile "' + $log + '" -screen-fullscreen 0 -screen-width 1280 -screen-height 800'
    }
    if ($Case -in 'ao','both' -and -not $UseVanillaWeather) { $argLine += ' -ds-natural-time' }
    if ($Case -in 'ce','both') { $argLine += ' -ds-require-ce' }
    if ($NoReload) { $argLine += ' -ds-no-reload' }
    Write-Output "Profile: $profile"
    Write-Output "Mods: $($mods -join ', ')"
    $proc = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $profile 'console.log') -RedirectStandardError (Join-Path $profile 'console-error.log')
    $null = $proc.Handle
    if ($gameVersion -eq '1.7') { & (Join-Path $PSScriptRoot '../Enable-TestRendering.ps1') -GameProcess $proc }
    if (!$proc.WaitForExit($TimeoutSeconds * 1000)) {
        $proc.Kill()
        $proc.WaitForExit()
        throw "Game timeout after $TimeoutSeconds seconds; profile $profile"
    }
    Write-Output "Exit code: $($proc.ExitCode)"
    if (Test-Path $result) { Get-Content $result }
    if (Test-Path $log) { Write-Output "Log: $log" }
    if ($proc.ExitCode -ne 0) { throw "Game exited $($proc.ExitCode); profile $profile" }
    if (-not (Test-Path $result) -or -not ((Get-Content $result -Tail 1) -match ' PASS;')) { throw "Harness did not report PASS; profile $profile" }
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit() }
    if ($createdLink -and (Test-Path -LiteralPath $link)) {
        $item = Get-Item -LiteralPath $link -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or $item.Target -ne $resolvedHarness) { throw "Unexpected harness link target; left intact: $link" }
        [IO.Directory]::Delete($link, $false)
    }
}
