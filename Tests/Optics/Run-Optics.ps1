param([int]$TimeoutSeconds = 420)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..\..')).Path
$mod = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$profile = Join-Path $env:TEMP ('SearchlightOptics-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $profile 'Config'
$harness = Join-Path $profile 'HarnessMod'
$assemblyDir = Join-Path $harness 'Assemblies'
New-Item -ItemType Directory -Path $config, (Join-Path $harness 'About'), $assemblyDir -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'HarnessMod/About/About.xml') (Join-Path $harness 'About/About.xml')
dotnet build (Join-Path $PSScriptRoot 'OpticsHarness.csproj') "-p:RimWorldDir=$game" "-p:OutputPath=$assemblyDir\" "-p:BaseIntermediateOutputPath=$(Join-Path $profile 'obj')\" -v:q
if ($LASTEXITCODE -ne 0) { throw 'Optics harness build failed' }
$mods = @('brrainz.harmony', 'ludeon.rimworld', 'CETeam.CombatExtended', 'DirectedSearchlights.Searchlight', 'searchlight.opticsharness')
$items = ($mods | ForEach-Object { '    <li>' + $_ + '</li>' }) -join "`r`n"
$xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<ModsConfigData>`r`n  <version>1.6</version>`r`n  <activeMods>`r`n$items`r`n  </activeMods>`r`n</ModsConfigData>`r`n"
[IO.File]::WriteAllText((Join-Path $config 'ModsConfig.xml'), $xml, [Text.UTF8Encoding]::new($false))
$link = Join-Path $game 'Mods/SearchlightOpticsHarness'
$resolvedHarness = (Resolve-Path -LiteralPath $harness).Path
$createdLink = $false
try {
    if (Test-Path -LiteralPath $link) { throw "Refusing to replace existing path: $link" }
    New-Item -ItemType Junction -Path $link -Target $resolvedHarness | Out-Null
    $createdLink = $true
    $result = Join-Path $profile 'optics-results.txt'
    $log = Join-Path $profile 'Player.log'
    $argLine = '-quicktest -savedatafolder="' + $profile + '" -ds-optics-result="' + $result + '" -logFile "' + $log + '" -screen-width 1280 -screen-height 800'
    Write-Output "Profile: $profile"
    $proc = Start-Process -FilePath (Join-Path $game 'RimWorldWin64.exe') -ArgumentList $argLine -WorkingDirectory $game -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2; $proc.Refresh() }
    if (-not $proc.HasExited) { $proc.Kill(); throw "Game timeout; profile $profile" }
    Write-Output "Exit code: $($proc.ExitCode)"
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    if ($proc.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or (Get-Content -LiteralPath $result -Tail 1) -notmatch ' PASS;') { throw "Optics game did not pass; profile $profile" }
}
finally {
    if ($createdLink -and (Test-Path -LiteralPath $link)) {
        $item = Get-Item -LiteralPath $link -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or $item.Target -ne $resolvedHarness) { throw "Unexpected junction target; left intact: $link" }
        [IO.Directory]::Delete($link, $false)
    }
}
